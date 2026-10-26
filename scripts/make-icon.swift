// 產生 Windows 版圖示的各尺寸 PNG（設計與 Mac 版 AppIcon 相同）：swift scripts/make-icon.swift <輸出資料夾>
// 再用 python3 scripts/pack-ico.py <輸出資料夾> Resources/AppIcon.ico 打包成 .ico
import AppKit

let outDir = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon-png")
try! FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = CGFloat(px)
    // Windows 的圖示不像 macOS 要留那麼大的邊，留 3% 讓工作列上看起來夠大
    let inset = s * 0.03
    let rect = NSRect(x: inset, y: inset, width: s - inset * 2, height: s - inset * 2)
    let bg = NSBezierPath(roundedRect: rect, xRadius: rect.width * 0.225, yRadius: rect.width * 0.225)
    NSGradient(colors: [NSColor(calibratedRed: 0.20, green: 0.78, blue: 0.45, alpha: 1),
                        NSColor(calibratedRed: 0.05, green: 0.45, blue: 0.75, alpha: 1)])!
        .draw(in: bg, angle: -90)
    // 手機外框
    let pw = rect.width * 0.40, ph = rect.width * 0.70
    let phone = NSRect(x: rect.midX - pw / 2, y: rect.midY - ph / 2, width: pw, height: ph)
    let body = NSBezierPath(roundedRect: phone, xRadius: pw * 0.18, yRadius: pw * 0.18)
    NSColor.white.setFill(); body.fill()
    let screen = phone.insetBy(dx: pw * 0.08, dy: pw * 0.08)
    NSColor(calibratedRed: 0.08, green: 0.12, blue: 0.2, alpha: 1).setFill()
    NSBezierPath(roundedRect: screen, xRadius: pw * 0.11, yRadius: pw * 0.11).fill()
    // 螢幕上的四格 App
    let g = screen.width * 0.26, gap = screen.width * 0.12
    let colors: [NSColor] = [.systemGreen, .systemOrange, .systemPink, .systemTeal]
    for i in 0..<4 {
        let col = CGFloat(i % 2), row = CGFloat(i / 2)
        let x = screen.midX - g - gap / 2 + col * (g + gap)
        let y = screen.midY + gap / 2 - row * (g + gap)
        colors[i].setFill()
        NSBezierPath(roundedRect: NSRect(x: x, y: y, width: g, height: g), xRadius: g * 0.25, yRadius: g * 0.25).fill()
    }
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for px in [16, 20, 24, 32, 40, 48, 64, 96, 128, 256] {
    try! render(px).write(to: outDir.appendingPathComponent("\(px).png"))
}
