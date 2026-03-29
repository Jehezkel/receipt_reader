package main

import (
	"image"
	"image/color"
	"testing"
)

func TestDetectReceiptBoundsFindsDimReceipt(t *testing.T) {
	canvas := image.NewNRGBA(image.Rect(0, 0, 1200, 1800))
	fillRect(canvas, canvas.Bounds(), color.NRGBA{R: 92, G: 72, B: 58, A: 255})

	receipt := image.Rect(260, 120, 890, 1650)
	fillRect(canvas, receipt, color.NRGBA{R: 205, G: 195, B: 184, A: 255})
	drawReceiptLines(canvas, receipt)

	bounds, ok := detectReceiptBounds(canvas)
	if !ok {
		t.Fatalf("expected receipt crop to be detected")
	}

	assertCloseToReceipt(t, bounds, receipt, 70)
}

func TestDetectReceiptBoundsRejectsUniformFrame(t *testing.T) {
	canvas := image.NewNRGBA(image.Rect(0, 0, 900, 1400))
	fillRect(canvas, canvas.Bounds(), color.NRGBA{R: 118, G: 118, B: 118, A: 255})

	if bounds, ok := detectReceiptBounds(canvas); ok {
		t.Fatalf("expected no crop for uniform frame, got %+v", bounds)
	}
}

func TestDetectReceiptBoundsFallsBackToTextRegion(t *testing.T) {
	canvas := image.NewNRGBA(image.Rect(0, 0, 1400, 2000))
	fillRect(canvas, canvas.Bounds(), color.NRGBA{R: 245, G: 244, B: 242, A: 255})

	receipt := image.Rect(120, 80, 620, 1880)
	fillRect(canvas, receipt, color.NRGBA{R: 239, G: 237, B: 232, A: 255})
	for y := receipt.Min.Y + 110; y < receipt.Max.Y-120; y += 54 {
		for x := receipt.Min.X + 36; x < receipt.Max.X-42; x++ {
			if (x/21)%6 == 0 {
				continue
			}
			for stroke := 0; stroke < 5; stroke++ {
				canvas.SetNRGBA(x, y+stroke, color.NRGBA{R: 128, G: 116, B: 104, A: 255})
			}
		}
	}

	bounds, ok := detectReceiptBounds(canvas)
	if !ok {
		t.Fatalf("expected crop from text-region fallback")
	}

	assertCloseToReceipt(t, bounds, receipt, 120)
}

func fillRect(img *image.NRGBA, rect image.Rectangle, fill color.NRGBA) {
	for y := rect.Min.Y; y < rect.Max.Y; y++ {
		for x := rect.Min.X; x < rect.Max.X; x++ {
			img.SetNRGBA(x, y, fill)
		}
	}
}

func drawReceiptLines(img *image.NRGBA, receipt image.Rectangle) {
	for line := receipt.Min.Y + 80; line < receipt.Max.Y-80; line += 66 {
		for y := line; y < minInt(receipt.Max.Y, line+10); y++ {
			for x := receipt.Min.X + 60; x < receipt.Max.X-60; x++ {
				if (x/32)%5 == 0 {
					continue
				}
				img.SetNRGBA(x, y, color.NRGBA{R: 72, G: 58, B: 44, A: 255})
			}
		}
	}
}

func assertCloseToReceipt(t *testing.T, actual image.Rectangle, expected image.Rectangle, tolerance int) {
	t.Helper()

	if absInt(actual.Min.X-expected.Min.X) > tolerance {
		t.Fatalf("left edge too far off: got %d want %d +/- %d", actual.Min.X, expected.Min.X, tolerance)
	}
	if absInt(actual.Min.Y-expected.Min.Y) > tolerance {
		t.Fatalf("top edge too far off: got %d want %d +/- %d", actual.Min.Y, expected.Min.Y, tolerance)
	}
	if absInt(actual.Max.X-expected.Max.X) > tolerance {
		t.Fatalf("right edge too far off: got %d want %d +/- %d", actual.Max.X, expected.Max.X, tolerance)
	}
	if absInt(actual.Max.Y-expected.Max.Y) > tolerance {
		t.Fatalf("bottom edge too far off: got %d want %d +/- %d", actual.Max.Y, expected.Max.Y, tolerance)
	}
}

func absInt(value int) int {
	if value < 0 {
		return -value
	}

	return value
}
