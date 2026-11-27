#include <winpr/windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <freerdp/client.h>
#include <freerdp/client/cliprdr.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/client/disp.h>
#include <freerdp/error.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/input.h>
#include <freerdp/settings.h>
#include <winpr/wlog.h>

/* The pipe carries little-endian (type, length) headers and up to 1 MiB per message.
 * The mapping carries (generation, width, height) and tightly packed BGRX pixels. */
enum
{
    Config = 1, Mouse, Key, Resize, LocalClipboard, TrustCertificate, Focus, Stop, Credentials,
    Connected = 101, Error, Certificate, RemoteClipboard, Heartbeat, Authenticate
};
enum { MaxMessage = 1024 * 1024, MaxSide = 8192, MaxArgs = 64 };

typedef struct
{
    uint32_t type;
    uint32_t size;
} Message;

typedef struct
{
    uint32_t generation, width, height;
    BYTE pixels[1];
} Frame;

static HANDLE pipe, frame_mutex;
static Frame* frame;
static CRITICAL_SECTION pipe_lock, clipboard_lock;
static DispClientContext* display;
static CliprdrClientContext* clipboard;
static WCHAR* clipboard_text;
static BOOL clipboard_ready, display_ready, frame_dirty;
static uint32_t wanted_width, wanted_height, wanted_dpi;
static uint32_t sent_width, sent_height, sent_dpi;

static BOOL transfer(void* data, DWORD size, BOOL writing)
{
    BYTE* bytes = data;
    while (size)
    {
        DWORD count = 0;
        BOOL ok = writing ? WriteFile(pipe, bytes, size, &count, NULL)
                          : ReadFile(pipe, bytes, size, &count, NULL);
        if (!ok || !count)
            return FALSE;
        bytes += count;
        size -= count;
    }
    return TRUE;
}

static BOOL send_event(uint32_t type, const void* data, uint32_t size)
{
    Message header = { type, size };
    EnterCriticalSection(&pipe_lock);
    BOOL ok = transfer(&header, sizeof(header), TRUE) &&
              transfer((void*)data, size, TRUE);
    LeaveCriticalSection(&pipe_lock);
    return ok;
}

static BOOL receive(Message* header, BYTE* data)
{
    return transfer(header, sizeof(*header), FALSE) && header->size <= MaxMessage &&
           transfer(data, header->size, FALSE);
}

static BOOL receive_reply(uint32_t type, Message* header, BYTE* data)
{
    while (receive(header, data) && header->type != Stop)
    {
        if (header->type == type)
            return TRUE;
    }
    return FALSE;
}

static void copy_frame(rdpGdi* gdi)
{
    if (!frame_dirty || !gdi || !gdi->primary_buffer || gdi->width < 1 || gdi->height < 1 ||
        gdi->width > MaxSide || gdi->height > MaxSide)
        return;
    const DWORD lock = WaitForSingleObject(frame_mutex, 0);
    if (lock != WAIT_OBJECT_0 && lock != WAIT_ABANDONED)
        return;

    const size_t width = (size_t)gdi->width * 4;
    for (int y = 0; y < gdi->height; ++y)
        memcpy(frame->pixels + y * width, gdi->primary_buffer + y * (size_t)gdi->stride, width);
    frame->width = (uint32_t)gdi->width;
    frame->height = (uint32_t)gdi->height;
    ++frame->generation;
    ReleaseMutex(frame_mutex);
    frame_dirty = FALSE;
}

static BOOL begin_paint(rdpContext* context)
{
    context->gdi->primary->hdc->hwnd->invalid->null = TRUE;
    return TRUE;
}

static BOOL end_paint(rdpContext* context)
{
    (void)context;
    frame_dirty = TRUE;
    return TRUE;
}

static BOOL desktop_resize(rdpContext* context)
{
    rdpSettings* settings = context->settings;
    frame_dirty = TRUE;
    return gdi_resize(context->gdi, freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth),
                      freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight));
}

static UINT display_caps(DispClientContext* context, UINT32 count, UINT32 factor_a, UINT32 factor_b)
{
    (void)context;
    (void)factor_a;
    (void)factor_b;
    display_ready = count > 0;
    return CHANNEL_RC_OK;
}

static void send_display_layout(void)
{
    if (!display_ready || !display || !wanted_width ||
        (wanted_width == sent_width && wanted_height == sent_height && wanted_dpi == sent_dpi))
        return;
    UINT32 scale = wanted_dpi * 100 / 96;
    DISPLAY_CONTROL_MONITOR_LAYOUT layout = { 0 };
    layout.Flags = DISPLAY_CONTROL_MONITOR_PRIMARY;
    layout.Width = wanted_width;
    layout.Height = wanted_height;
    layout.PhysicalWidth = (wanted_width * 254 + 5 * wanted_dpi) / (10 * wanted_dpi);
    layout.PhysicalHeight = (wanted_height * 254 + 5 * wanted_dpi) / (10 * wanted_dpi);
    if (layout.PhysicalWidth < 10) layout.PhysicalWidth = 10;
    if (layout.PhysicalHeight < 10) layout.PhysicalHeight = 10;
    if (layout.PhysicalWidth > 10000) layout.PhysicalWidth = 10000;
    if (layout.PhysicalHeight > 10000) layout.PhysicalHeight = 10000;
    layout.DesktopScaleFactor = scale < 100 ? 100 : (scale > 500 ? 500 : scale);
    layout.DeviceScaleFactor = scale >= 180 ? 180 : (scale >= 140 ? 140 : 100);
    sent_width = wanted_width;
    sent_height = wanted_height;
    sent_dpi = wanted_dpi;
    (void)display->SendMonitorLayout(display, 1, &layout);
}

static UINT offer_clipboard(CliprdrClientContext* context)
{
    CLIPRDR_FORMAT format = { CF_UNICODETEXT, NULL };
    CLIPRDR_FORMAT_LIST list = { 0 };
    list.numFormats = clipboard_text ? 1 : 0;
    list.formats = &format;
    return context->ClientFormatList(context, &list);
}

static UINT clipboard_monitor_ready(CliprdrClientContext* context, const CLIPRDR_MONITOR_READY* ready)
{
    (void)ready;
    CLIPRDR_GENERAL_CAPABILITY_SET general = {
        CB_CAPSTYPE_GENERAL, CB_CAPSTYPE_GENERAL_LEN, CB_CAPS_VERSION_2, CB_USE_LONG_FORMAT_NAMES
    };
    CLIPRDR_CAPABILITIES caps = { 0 };
    caps.cCapabilitiesSets = 1;
    caps.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&general;
    UINT rc = context->ClientCapabilities(context, &caps);
    if (rc != CHANNEL_RC_OK)
        return rc;
    EnterCriticalSection(&clipboard_lock);
    clipboard_ready = TRUE;
    rc = offer_clipboard(context);
    LeaveCriticalSection(&clipboard_lock);
    return rc;
}

static UINT clipboard_format_list(CliprdrClientContext* context, const CLIPRDR_FORMAT_LIST* formats)
{
    CLIPRDR_FORMAT_LIST_RESPONSE response = { 0 };
    response.common.msgFlags = CB_RESPONSE_OK;
    UINT rc = context->ClientFormatListResponse(context, &response);
    if (rc != CHANNEL_RC_OK)
        return rc;
    for (UINT32 i = 0; i < formats->numFormats; ++i)
    {
        if (formats->formats[i].formatId == CF_UNICODETEXT)
        {
            CLIPRDR_FORMAT_DATA_REQUEST request = { 0 };
            request.requestedFormatId = CF_UNICODETEXT;
            return context->ClientFormatDataRequest(context, &request);
        }
    }
    return CHANNEL_RC_OK;
}

static UINT clipboard_data_request(CliprdrClientContext* context,
                                   const CLIPRDR_FORMAT_DATA_REQUEST* request)
{
    CLIPRDR_FORMAT_DATA_RESPONSE response = { 0 };
    EnterCriticalSection(&clipboard_lock);
    if (request->requestedFormatId == CF_UNICODETEXT && clipboard_text)
    {
        response.common.msgFlags = CB_RESPONSE_OK;
        response.common.dataLen = (UINT32)((wcslen(clipboard_text) + 1) * sizeof(WCHAR));
        response.requestedFormatData = (BYTE*)clipboard_text;
    }
    else
        response.common.msgFlags = CB_RESPONSE_FAIL;
    const UINT rc = context->ClientFormatDataResponse(context, &response);
    LeaveCriticalSection(&clipboard_lock);
    return rc;
}

static UINT clipboard_data_response(CliprdrClientContext* context,
                                    const CLIPRDR_FORMAT_DATA_RESPONSE* response)
{
    (void)context;
    if (!(response->common.msgFlags & CB_RESPONSE_OK) || response->common.dataLen < sizeof(WCHAR) ||
        response->common.dataLen > MaxMessage || response->common.dataLen % sizeof(WCHAR))
        return CHANNEL_RC_OK;
    const int count = (int)(response->common.dataLen / sizeof(WCHAR));
    const WCHAR* text = (const WCHAR*)response->requestedFormatData;
    if (!text || text[count - 1] != L'\0')
        return CHANNEL_RC_OK;
    const int length = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    if (length <= 0 || length > MaxMessage)
        return CHANNEL_RC_OK;
    char* utf8 = malloc((size_t)length);
    if (utf8 && WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, length, NULL, NULL))
        (void)send_event(RemoteClipboard, utf8, (uint32_t)length - 1);
    free(utf8);
    return CHANNEL_RC_OK;
}

static void set_clipboard(const BYTE* data, uint32_t size)
{
    const int length = size ? MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
                                                 (const char*)data, (int)size, NULL, 0) : 0;
    WCHAR* text = length ? calloc((size_t)length + 1, sizeof(WCHAR)) : NULL;
    if (text)
        (void)MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)data,
                                  (int)size, text, length);
    EnterCriticalSection(&clipboard_lock);
    free(clipboard_text);
    clipboard_text = text;
    if (clipboard_ready && clipboard)
        (void)offer_clipboard(clipboard);
    LeaveCriticalSection(&clipboard_lock);
}

static void channel_connected(void* context, const ChannelConnectedEventArgs* event)
{
    if (strcmp(event->name, DISP_DVC_CHANNEL_NAME) == 0)
    {
        display = event->pInterface;
        display->DisplayControlCaps = display_caps;
    }
    else if (strcmp(event->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
    {
        EnterCriticalSection(&clipboard_lock);
        clipboard = event->pInterface;
        clipboard->MonitorReady = clipboard_monitor_ready;
        clipboard->ServerFormatList = clipboard_format_list;
        clipboard->ServerFormatDataRequest = clipboard_data_request;
        clipboard->ServerFormatDataResponse = clipboard_data_response;
        LeaveCriticalSection(&clipboard_lock);
    }
    else
        freerdp_client_OnChannelConnectedEventHandler(context, event);
}

static void channel_disconnected(void* context, const ChannelDisconnectedEventArgs* event)
{
    if (strcmp(event->name, DISP_DVC_CHANNEL_NAME) == 0)
    {
        display_ready = FALSE;
        display = NULL;
        sent_width = 0;
        sent_height = 0;
    }
    else if (strcmp(event->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
    {
        EnterCriticalSection(&clipboard_lock);
        clipboard_ready = FALSE;
        clipboard = NULL;
        LeaveCriticalSection(&clipboard_lock);
    }
    else
        freerdp_client_OnChannelDisconnectedEventHandler(context, event);
}

static BOOL post_connect(freerdp* instance)
{
    rdpContext* context = instance->context;
    if (!context->gdi && !gdi_init(instance, PIXEL_FORMAT_BGRX32))
        return FALSE;
    context->update->BeginPaint = begin_paint;
    context->update->EndPaint = end_paint;
    context->update->DesktopResize = desktop_resize;
    sent_width = (uint32_t)context->gdi->width;
    sent_height = (uint32_t)context->gdi->height;
    sent_dpi = 96;
    frame_dirty = TRUE;
    return TRUE;
}

static DWORD check_certificate(const char* host, UINT16 port, const char* fingerprint,
                               BOOL changed)
{
    char prompt[1024];
    const int length = snprintf(prompt, sizeof(prompt), "%u\n%s:%u\n%s", changed ? 1u : 0u,
                                host ? host : "", (unsigned)port, fingerprint ? fingerprint : "");
    if (length < 0 || length >= sizeof(prompt) ||
        !send_event(Certificate, prompt, (uint32_t)length))
        return 0;
    Message header;
    BYTE* data = malloc(MaxMessage);
    DWORD decision = 0;
    if (!data)
        return 0;
    /* A resize or local input may already be queued when the certificate arrives. */
    if (receive_reply(TrustCertificate, &header, data) && header.size == 1)
        decision = data[0] ? 2 : 0;
    free(data);
    return decision;
}

static BOOL authenticate(freerdp* instance, char** username, char** password, char** domain,
                         rdp_auth_reason reason)
{
    (void)instance;
    if (reason == AUTH_TLS || reason == AUTH_RDP)
        return TRUE; /* xrdp can show its own sign-in screen. */
    if (reason != AUTH_NLA && reason != AUTH_RDSTLS && reason != GW_AUTH_HTTP &&
        reason != GW_AUTH_RDG && reason != GW_AUTH_RPC)
        return FALSE;
    const BOOL gateway = reason == GW_AUTH_HTTP || reason == GW_AUTH_RDG || reason == GW_AUTH_RPC;
    const BYTE request = gateway ? 1 : 0;
    if (!send_event(Authenticate, &request, 1))
        return FALSE;

    BYTE* data = malloc(MaxMessage);
    Message reply;
    BOOL ok = FALSE;
    if (data && receive_reply(Credentials, &reply, data) && reply.size > 0)
    {
        char** fields[] = { username, domain, password };
        char* values[3] = { NULL };
        uint32_t offset = 0;
        for (int i = 0; i < 3 && offset < reply.size; ++i)
        {
            BYTE* end = memchr(data + offset, '\0', reply.size - offset);
            if (!end) break;
            values[i] = _strdup((const char*)data + offset);
            offset = (uint32_t)(end - data) + 1;
        }
        ok = values[0] && values[1] && values[2] && offset == reply.size;
        if (ok)
        {
            if (*password) SecureZeroMemory(*password, strlen(*password));
            for (int i = 0; i < 3; ++i)
            {
                free(*fields[i]);
                *fields[i] = values[i];
            }
        }
        else
        {
            if (values[2]) SecureZeroMemory(values[2], strlen(values[2]));
            for (int i = 0; i < 3; ++i) free(values[i]);
        }
    }
    if (data) SecureZeroMemory(data, MaxMessage);
    free(data);
    return ok;
}

static DWORD verify_certificate(freerdp* instance, const char* host, UINT16 port, const char* name,
                                const char* subject, const char* issuer, const char* fingerprint,
                                DWORD flags)
{
    (void)instance; (void)name; (void)subject; (void)issuer; (void)flags;
    return check_certificate(host, port, fingerprint, FALSE);
}

static DWORD verify_changed_certificate(freerdp* instance, const char* host, UINT16 port,
                                        const char* name, const char* subject, const char* issuer,
                                        const char* fingerprint, const char* old_subject,
                                        const char* old_issuer, const char* old_fingerprint,
                                        DWORD flags)
{
    (void)old_subject; (void)old_issuer; (void)old_fingerprint;
    (void)instance; (void)name; (void)subject; (void)issuer; (void)flags;
    return check_certificate(host, port, fingerprint, TRUE);
}

static BOOL apply_config(rdpSettings* settings, BYTE* bytes, uint32_t length)
{
    char* args[MaxArgs] = { "SshManagerRdp" };
    const char* folders[16];
    size_t folder_count = 0;
    int count = 1;
    uint32_t offset = 0;
    while (offset < length && count < MaxArgs)
    {
        char* argument = (char*)bytes + offset;
        BYTE* end = memchr(bytes + offset, '\0', length - offset);
        if (!end)
            return FALSE;
        if (strncmp(argument, "/sshm-folder:", 13) == 0)
        {
            if (folder_count == ARRAYSIZE(folders))
                return FALSE;
            folders[folder_count++] = argument + 13;
        }
        else
            args[count++] = argument;
        offset = (uint32_t)(end - bytes) + 1;
    }
    BOOL ok = (offset == length &&
        freerdp_client_settings_parse_command_line(settings, count, args, FALSE) == 0);
    for (size_t i = 0; ok && i < folder_count; ++i)
    {
        char name[16];
        (void)snprintf(name, sizeof(name), "Folder%u", (unsigned)(i + 1));
        const char* device[] = { "drive", name, folders[i] };
        ok = freerdp_client_add_device_channel(settings, ARRAYSIZE(device), device);
    }
    SecureZeroMemory(bytes, length);
    return ok;
}

static BOOL apply_command(rdpContext* context, const Message* msg, const BYTE* data)
{
    uint32_t values[3] = { 0 };
    if (msg->size <= sizeof(values))
        memcpy(values, data, msg->size);
    switch (msg->type)
    {
        case Stop:
            return FALSE;
        case Mouse:
            if (msg->size == 12)
            {
                if (values[0] & 0x10000)
                    (void)context->input->ExtendedMouseEvent(context->input, (UINT16)values[0],
                                                              (UINT16)values[1], (UINT16)values[2]);
                else
                    (void)context->input->MouseEvent(context->input, (UINT16)values[0],
                                                      (UINT16)values[1], (UINT16)values[2]);
            }
            break;
        case Key:
            if (msg->size == 8)
                (void)context->input->KeyboardEvent(context->input, (UINT16)values[0], (UINT8)values[1]);
            break;
        case Resize:
            if (msg->size == 12 && values[0] >= 640 && values[0] <= MaxSide &&
                values[1] >= 480 && values[1] <= MaxSide && values[2] >= 96 && values[2] <= 960)
            {
                wanted_width = values[0] & ~1u;
                wanted_height = values[1];
                wanted_dpi = values[2];
            }
            break;
        case LocalClipboard:
            set_clipboard(data, msg->size);
            break;
        case Focus:
            if (msg->size == 4)
                (void)context->input->FocusInEvent(context->input, (UINT16)values[0]);
            break;
    }
    return TRUE;
}

static BOOL run_session(freerdp* instance, BYTE* data)
{
    rdpContext* context = instance->context;
    if (!freerdp_connect(instance) || !send_event(Connected, NULL, 0))
        return FALSE;

    ULONGLONG last_frame = 0, last_heartbeat = GetTickCount64();
    while (!freerdp_shall_disconnect_context(context))
    {
        HANDLE handles[MAXIMUM_WAIT_OBJECTS];
        const DWORD count = freerdp_get_event_handles(context, handles, MAXIMUM_WAIT_OBJECTS);
        if (!count || WaitForMultipleObjects(count, handles, FALSE, 16) == WAIT_FAILED)
            return FALSE;
        if (!freerdp_check_event_handles(context))
        {
            if (!client_auto_reconnect(instance))
                return FALSE;
            frame_dirty = TRUE;
        }

        for (int i = 0; i < 64; ++i)
        {
            Message next;
            DWORD peeked = 0;
            if (!PeekNamedPipe(pipe, &next, sizeof(next), &peeked, NULL, NULL))
                return TRUE;
            if (peeked != sizeof(next))
                break;
            if (next.size > MaxMessage)
                return FALSE;
            if (!receive(&next, data) || !apply_command(context, &next, data))
                return TRUE;
        }

        const ULONGLONG now = GetTickCount64();
        send_display_layout();
        if (now - last_frame >= 30)
        {
            copy_frame(context->gdi);
            last_frame = now;
        }
        if (now - last_heartbeat >= 1000)
        {
            if (!send_event(Heartbeat, NULL, 0))
                return TRUE;
            last_heartbeat = now;
        }
    }
    return freerdp_get_last_error(context) == FREERDP_ERROR_SUCCESS;
}

int main(int argc, char** argv)
{
    if (argc != 2 || strlen(argv[1]) != 32 || strspn(argv[1], "0123456789abcdef") != 32)
        return 2;
    char pipe_name[90], mapping_name[90], mutex_name[90];
    (void)snprintf(pipe_name, sizeof(pipe_name), "\\\\.\\pipe\\SshManagerRdp-%s", argv[1]);
    (void)snprintf(mapping_name, sizeof(mapping_name), "Local\\SshManagerRdpFrame-%s", argv[1]);
    (void)snprintf(mutex_name, sizeof(mutex_name), "Local\\SshManagerRdpMutex-%s", argv[1]);

    pipe = CreateFileA(pipe_name, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING,
                       SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, NULL);
    HANDLE mapping = OpenFileMappingA(FILE_MAP_WRITE, FALSE, mapping_name);
    frame_mutex = OpenMutexA(SYNCHRONIZE | MUTEX_MODIFY_STATE, FALSE, mutex_name);
    if (pipe == INVALID_HANDLE_VALUE || !mapping || !frame_mutex)
        return 2;
    frame = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, 0);
    if (!frame)
        return 2;

    InitializeCriticalSection(&pipe_lock);
    InitializeCriticalSection(&clipboard_lock);
    WLog_SetLogLevel(WLog_GetRoot(), WLOG_ERROR);
    WSADATA sockets;
    const BOOL socket_ready = WSAStartup(MAKEWORD(2, 2), &sockets) == 0;
    RDP_CLIENT_ENTRY_POINTS entry = { 0 };
    entry.Size = sizeof(entry);
    entry.Version = RDP_CLIENT_INTERFACE_VERSION;
    entry.ContextSize = sizeof(rdpClientContext);
    rdpContext* context = socket_ready ? freerdp_client_context_new(&entry) : NULL;
    BYTE* data = malloc(MaxMessage);
    Message initial;
    BOOL ok = FALSE;
    if (context && data && receive(&initial, data) && initial.type == Config &&
        apply_config(context->settings, data, initial.size) &&
        PubSub_SubscribeChannelConnected(context->pubSub, channel_connected) >= 0 &&
        PubSub_SubscribeChannelDisconnected(context->pubSub, channel_disconnected) >= 0)
    {
        freerdp* instance = context->instance;
        instance->PostConnect = post_connect;
        instance->VerifyCertificateEx = verify_certificate;
        instance->VerifyChangedCertificateEx = verify_changed_certificate;
        instance->AuthenticateEx = authenticate;
        ok = run_session(instance, data);
        if (!ok)
        {
            DWORD code = freerdp_get_last_error(context);
            (void)send_event(Error, &code, sizeof(code));
        }
        (void)freerdp_disconnect(instance);
        if (context->gdi)
            gdi_free(instance);
    }
    else
    {
        DWORD code = 0;
        (void)send_event(Error, &code, sizeof(code));
    }
    if (data) SecureZeroMemory(data, MaxMessage);
    free(data);
    if (context) freerdp_client_context_free(context);
    if (socket_ready) WSACleanup();
    free(clipboard_text);
    DeleteCriticalSection(&clipboard_lock);
    DeleteCriticalSection(&pipe_lock);
    UnmapViewOfFile(frame);
    CloseHandle(mapping);
    CloseHandle(frame_mutex);
    CloseHandle(pipe);
    return ok ? 0 : 1;
}
