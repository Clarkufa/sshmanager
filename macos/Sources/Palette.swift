import AppKit
import SwiftUI

/// Colours, glyphs and button styles shared with the Windows app (docs/ui-spec.md §1).
struct Palette {
    let canvas, surface, ink, muted, line, selection, mint, teal, danger, primary, primaryHover, disabled: Color

    static let light = Palette(canvas: Color(hex: 0xF6F8FB), surface: Color(hex: 0xFFFFFF), ink: Color(hex: 0x102235),
                               muted: Color(hex: 0x485C6D), line: Color(hex: 0xDFE6EE), selection: Color(hex: 0xEAF2FA),
                               mint: Color(hex: 0xE7F6F2), teal: Color(hex: 0x00645C), danger: Color(hex: 0xA42435),
                               primary: Color(hex: 0x00645C), primaryHover: Color(hex: 0x00786B), disabled: Color(hex: 0x7E8F9E))
    static let dark = Palette(canvas: Color(hex: 0x171F2B), surface: Color(hex: 0x202B3A), ink: Color(hex: 0xE9EFF8),
                              muted: Color(hex: 0xAABACF), line: Color(hex: 0x3B4C60), selection: Color(hex: 0x2A4359),
                              mint: Color(hex: 0x21413E), teal: Color(hex: 0x85E1CC), danger: Color(hex: 0xFFA3AC),
                              primary: Color(hex: 0x146859), primaryHover: Color(hex: 0x197565), disabled: Color(hex: 0x899BB0))

    static func of(_ scheme: ColorScheme) -> Palette { scheme == .dark ? dark : light }

    /// The terminal and the session area keep these colours in both themes.
    static let terminalBackground = Color(hex: 0x111A27)
    static let terminalText = Color(hex: 0xDCDCDC)
}

extension Color {
    init(hex: UInt32) {
        self.init(.sRGB, red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255, opacity: 1)
    }
}

/// The Windows glyphs (src/Theme.cs Icon): 16×16 line drawings, 1.5 pt stroke, round caps and joins.
enum Glyph: String {
    case plus, refresh, edit, delete, terminal, server, folder, move, moveLeft, copy, key, browse, moon, sun, dot, star, settings

    /// Filled shapes; the others are strokes.
    var filled: Bool { self == .dot || self == .star }

    var path: Path {
        var path = Path()
        func trace(_ points: CGFloat...) {
            path.move(to: CGPoint(x: points[0], y: points[1]))
            var index = 2
            while index + 1 < points.count {
                path.addLine(to: CGPoint(x: points[index], y: points[index + 1]))
                index += 2
            }
        }
        switch self {
        case .plus: trace(8, 3, 8, 13); trace(3, 8, 13, 8)
        case .refresh:
            path.addArc(center: CGPoint(x: 8, y: 8), radius: 6, startAngle: .degrees(25), endAngle: .degrees(335), clockwise: false)
            trace(14, 2, 14, 6, 10, 6)
        case .edit: trace(3, 10, 11, 2, 14, 5, 6, 13, 2, 14, 3, 10); trace(9, 4, 12, 7)
        case .delete: trace(4, 6, 5, 14, 11, 14, 12, 6); trace(2, 4, 14, 4); trace(6, 4, 6, 2, 10, 2, 10, 4)
        case .terminal: trace(3, 4, 7, 8, 3, 12); trace(9, 12, 14, 12)
        case .server:
            path.addRect(CGRect(x: 2, y: 2, width: 12, height: 5))
            path.addRect(CGRect(x: 2, y: 9, width: 12, height: 5))
            trace(10, 4.5, 11, 4.5); trace(10, 11.5, 11, 11.5)
        case .folder: trace(2, 13, 2, 3, 6, 3, 8, 5, 14, 5, 14, 13, 2, 13)
        case .move: trace(2, 8, 14, 8); trace(10, 4, 14, 8, 10, 12)
        case .moveLeft: trace(2, 8, 14, 8); trace(6, 4, 2, 8, 6, 12)
        case .copy: path.addRect(CGRect(x: 5, y: 5, width: 9, height: 9)); trace(2, 11, 2, 2, 11, 2)
        case .key: path.addEllipse(in: CGRect(x: 2, y: 2, width: 7, height: 7)); trace(8, 8, 14, 14, 14, 11)
        case .browse: path.addEllipse(in: CGRect(x: 2, y: 2, width: 8, height: 8)); trace(9, 9, 14, 14)
        case .moon:
            path.move(to: CGPoint(x: 8, y: 2))
            path.addCurve(to: CGPoint(x: 8, y: 14), control1: CGPoint(x: 1, y: 2), control2: CGPoint(x: 0, y: 13))
            path.addCurve(to: CGPoint(x: 14, y: 9), control1: CGPoint(x: 11, y: 14), control2: CGPoint(x: 13, y: 12))
            path.addCurve(to: CGPoint(x: 8, y: 2), control1: CGPoint(x: 9, y: 12), control2: CGPoint(x: 5, y: 7))
            path.closeSubpath()
        case .sun:
            path.addEllipse(in: CGRect(x: 5, y: 5, width: 6, height: 6))
            trace(8, 1, 8, 3); trace(8, 13, 8, 15); trace(1, 8, 3, 8); trace(13, 8, 15, 8)
            trace(3, 3, 4, 4); trace(12, 12, 13, 13); trace(12, 4, 13, 3); trace(3, 13, 4, 12)
        case .dot: path.addEllipse(in: CGRect(x: 2, y: 2, width: 12, height: 12))
        case .star:
            for index in 0..<10 {
                let angle = Double.pi / 5 * Double(index) - Double.pi / 2
                let radius: Double = index % 2 == 0 ? 7 : 3
                let point = CGPoint(x: 8 + radius * cos(angle), y: 8.5 + radius * sin(angle))
                if index == 0 { path.move(to: point) } else { path.addLine(to: point) }
            }
            path.closeSubpath()
        case .settings:
            path.addEllipse(in: CGRect(x: 5.5, y: 5.5, width: 5, height: 5))
            for index in 0..<8 {
                let angle = Double.pi / 4 * Double(index)
                path.move(to: CGPoint(x: 8 + 4.8 * cos(angle), y: 8 + 4.8 * sin(angle)))
                path.addLine(to: CGPoint(x: 8 + 6.6 * cos(angle), y: 8 + 6.6 * sin(angle)))
            }
        }
        return path
    }
}

struct GlyphView: View {
    let glyph: Glyph
    var color: Color

    var body: some View {
        Canvas { context, _ in
            if glyph.filled {
                context.fill(glyph.path, with: .color(color))
            } else {
                context.stroke(glyph.path, with: .color(color),
                               style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
            }
        }
        .frame(width: 16, height: 16)
        .accessibilityHidden(true)
    }
}

enum ButtonKind { case primary, outline, quiet, tab, selectedTab }

/// Windows button styles: height 34, radius 7, glyph and text centred with a 6 pt gap.
struct WinButtonStyle: ButtonStyle {
    let kind: ButtonKind
    var danger = false
    var height: CGFloat = 32
    @Environment(\.colorScheme) private var scheme
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        let palette = Palette.of(scheme)
        let pressed = configuration.isPressed
        let fill: Color
        let text: Color
        switch kind {
        case .primary:
            fill = enabled ? (pressed ? palette.primaryHover : palette.primary) : palette.line
            text = enabled ? .white : palette.disabled
        case .outline:
            fill = pressed ? palette.selection : palette.surface
            text = enabled ? palette.ink : palette.disabled
        case .quiet:
            fill = pressed ? palette.selection : .clear
            text = enabled ? palette.ink : palette.disabled
        case .tab:
            fill = pressed ? palette.selection : .clear
            text = enabled ? palette.muted : palette.disabled
        case .selectedTab:
            fill = palette.mint
            text = palette.teal
        }
        return configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(danger && enabled ? palette.danger : text)
            .padding(.horizontal, 12)
            .frame(height: kind == .tab || kind == .selectedTab ? 30 : height)
            .background(RoundedRectangle(cornerRadius: 7).fill(fill))
            .overlay {
                if kind == .outline { RoundedRectangle(cornerRadius: 7).strokeBorder(palette.line, lineWidth: 1) }
            }
            .contentShape(RoundedRectangle(cornerRadius: 7))
    }
}

/// A label with a Windows glyph in front: Teal for ordinary buttons, white on primary, red when dangerous.
struct GlyphLabel: View {
    let title: String
    let glyph: Glyph?
    var kind: ButtonKind = .outline
    var danger = false
    @Environment(\.colorScheme) private var scheme
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        let palette = Palette.of(scheme)
        let tint: Color = !enabled ? palette.disabled : danger ? palette.danger : kind == .primary ? .white : palette.teal
        HStack(spacing: 6) {
            if let glyph { GlyphView(glyph: glyph, color: tint) }
            if !title.isEmpty { Text(title).lineLimit(1) }
        }
    }
}

extension View {
    func winButton(_ kind: ButtonKind, danger: Bool = false) -> some View {
        buttonStyle(WinButtonStyle(kind: kind, danger: danger))
    }
}

/// The app mark next to page titles.
struct AppMark: View {
    var body: some View {
        Image(nsImage: NSApp.applicationIconImage ?? NSImage())
            .resizable().interpolation(.high)
            .frame(width: 42, height: 42)
            .accessibilityHidden(true)
    }
}
