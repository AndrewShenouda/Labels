# Changelog

## 1.0.0 — 2026-11-03

First public release.

### Printing
- Prints to TSPL thermal label printers over **Bluetooth** (direct RFCOMM — no COM port needed) or **USB / any Windows printer** (raw jobs through the printer's own Windows driver).
- Label sizes: 4×6, 4×4, 4×3, 4×2, 3×2, 2.25×1.25, 2×1 in, or a custom size. 203 or 300 dpi.
- Darkness, speed, gap or continuous rolls, calibration, test label, feed.
- Only rows that contain ink are sent, which keeps Bluetooth printing fast.

### Labels
- Drop a PDF or image of any page size: the label is found, cropped and fitted automatically.
- Turned upright by reading the label's text (Windows' built-in, on-device text recognition).
- Pages with two labels are split into two.
- Carrier and tracking number detected (UPS, USPS, FedEx, DHL, Amazon, OnTrac) with copy and track buttons.
- Photos-style crop editor.
- Stamps: a built-in "Fragile — Handle With Care" stamp or any image, auto-placed in empty space, solid or faint.

### App
- Print history with one-click reprint.
- Suggests (or optionally auto-prints) new labels saved to Downloads.
- Light and dark mode, Liquid Glass–inspired design with spring animations.
