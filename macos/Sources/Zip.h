#include <stddef.h>
#include <stdint.h>
#include <sys/types.h>

// Public libarchive functions used by the ZIP adapter. The macOS SDK ships
// the link library without the upstream archive.h and archive_entry.h headers.
struct archive;
struct archive_entry;
struct archive *archive_read_new(void);
int archive_read_support_format_zip_seekable(struct archive *);
int archive_read_open_memory(struct archive *, const void *, size_t);
int archive_read_next_header(struct archive *, struct archive_entry **);
ssize_t archive_read_data(struct archive *, void *, size_t);
int archive_read_free(struct archive *);

struct archive *archive_write_new(void);
int archive_write_set_format_zip(struct archive *);
int archive_write_set_bytes_in_last_block(struct archive *, int);
int archive_write_open_memory(struct archive *, void *, size_t, size_t *);
int archive_write_header(struct archive *, struct archive_entry *);
ssize_t archive_write_data(struct archive *, const void *, size_t);
int archive_write_close(struct archive *);
int archive_write_free(struct archive *);

struct archive_entry *archive_entry_new(void);
void archive_entry_free(struct archive_entry *);
const char *archive_entry_pathname_utf8(struct archive_entry *);
const char *archive_entry_pathname(struct archive_entry *);
int64_t archive_entry_size(struct archive_entry *);
mode_t archive_entry_mode(struct archive_entry *);
const char *archive_entry_hardlink(struct archive_entry *);
void archive_entry_set_pathname_utf8(struct archive_entry *, const char *);
void archive_entry_set_size(struct archive_entry *, int64_t);
void archive_entry_set_mode(struct archive_entry *, mode_t);
