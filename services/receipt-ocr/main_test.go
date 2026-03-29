package main

import (
	"bytes"
	"encoding/binary"
	"image"
	"image/color"
	"image/jpeg"
	"slices"
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

func TestReadJPEGExifOrientation(t *testing.T) {
	imageBytes := jpegWithExifOrientation(t, 6)

	orientation, ok := readJPEGExifOrientation(imageBytes)
	if !ok {
		t.Fatalf("expected EXIF orientation to be detected")
	}

	if orientation != 6 {
		t.Fatalf("unexpected EXIF orientation: got %d want 6", orientation)
	}
}

func TestDecodeAndOrientReceiptImageAppliesExifRotation(t *testing.T) {
	imageBytes := jpegWithExifOrientation(t, 6)

	oriented, filters, notes, err := decodeAndOrientReceiptImage(imageBytes)
	if err != nil {
		t.Fatalf("decodeAndOrientReceiptImage returned error: %v", err)
	}

	if oriented.Bounds().Dx() != 20 || oriented.Bounds().Dy() != 40 {
		t.Fatalf("expected EXIF rotation to swap dimensions, got %dx%d", oriented.Bounds().Dx(), oriented.Bounds().Dy())
	}

	if !slices.Contains(filters, "exif-orientation-6") {
		t.Fatalf("expected EXIF filter to be recorded, got %v", filters)
	}

	if len(notes) == 0 {
		t.Fatalf("expected EXIF note to be recorded")
	}
}

func TestNormalizeReceiptOrientationRotatesLandscapeReceiptToPortrait(t *testing.T) {
	portrait := syntheticReceiptCanvas()
	landscape := rotate90NRGBA(portrait)

	normalized, rotationDegrees, rotated := normalizeReceiptOrientation(landscape)
	if !rotated {
		t.Fatalf("expected receipt orientation normalization to rotate the frame")
	}

	if rotationDegrees != 90 && rotationDegrees != 270 {
		t.Fatalf("expected a quarter-turn rotation, got %d", rotationDegrees)
	}

	if normalized.Bounds().Dy() <= normalized.Bounds().Dx() {
		t.Fatalf("expected normalized receipt to be portrait, got %dx%d", normalized.Bounds().Dx(), normalized.Bounds().Dy())
	}

	bounds, ok := detectReceiptBounds(normalized)
	if !ok {
		t.Fatalf("expected receipt crop to remain detectable after normalization")
	}

	if bounds.Dy() <= bounds.Dx() {
		t.Fatalf("expected detected receipt crop to be portrait, got %dx%d", bounds.Dx(), bounds.Dy())
	}
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

func syntheticReceiptCanvas() *image.NRGBA {
	canvas := image.NewNRGBA(image.Rect(0, 0, 900, 1400))
	fillRect(canvas, canvas.Bounds(), color.NRGBA{R: 84, G: 74, B: 66, A: 255})

	receipt := image.Rect(180, 70, 680, 1320)
	fillRect(canvas, receipt, color.NRGBA{R: 239, G: 236, B: 231, A: 255})

	header := image.Rect(receipt.Min.X+120, receipt.Min.Y+48, receipt.Max.X-120, receipt.Min.Y+120)
	fillRect(canvas, header, color.NRGBA{R: 62, G: 56, B: 48, A: 255})

	for y := receipt.Min.Y + 180; y < receipt.Max.Y-180; y += 56 {
		for x := receipt.Min.X + 44; x < receipt.Max.X-44; x++ {
			if (x/22)%7 == 0 {
				continue
			}
			for stroke := 0; stroke < 4; stroke++ {
				canvas.SetNRGBA(x, y+stroke, color.NRGBA{R: 88, G: 80, B: 72, A: 255})
			}
		}
	}

	footer := image.Rect(receipt.Min.X+60, receipt.Max.Y-140, receipt.Max.X-60, receipt.Max.Y-78)
	fillRect(canvas, footer, color.NRGBA{R: 70, G: 62, B: 55, A: 255})

	return canvas
}

func jpegWithExifOrientation(t *testing.T, orientation uint16) []byte {
	t.Helper()

	img := image.NewNRGBA(image.Rect(0, 0, 40, 20))
	fillRect(img, img.Bounds(), color.NRGBA{R: 230, G: 224, B: 216, A: 255})
	fillRect(img, image.Rect(3, 4, 14, 16), color.NRGBA{R: 32, G: 28, B: 24, A: 255})

	var jpegBuffer bytes.Buffer
	if err := jpeg.Encode(&jpegBuffer, img, &jpeg.Options{Quality: 85}); err != nil {
		t.Fatalf("jpeg encode failed: %v", err)
	}

	exifSegment := buildExifOrientationSegment(t, orientation)
	jpegBytes := jpegBuffer.Bytes()
	withExif := make([]byte, 0, len(jpegBytes)+len(exifSegment))
	withExif = append(withExif, jpegBytes[:2]...)
	withExif = append(withExif, exifSegment...)
	withExif = append(withExif, jpegBytes[2:]...)
	return withExif
}

func buildExifOrientationSegment(t *testing.T, orientation uint16) []byte {
	t.Helper()

	var tiff bytes.Buffer
	tiff.WriteString("MM")
	if err := binary.Write(&tiff, binary.BigEndian, uint16(42)); err != nil {
		t.Fatalf("could not write TIFF header: %v", err)
	}
	if err := binary.Write(&tiff, binary.BigEndian, uint32(8)); err != nil {
		t.Fatalf("could not write first IFD offset: %v", err)
	}
	if err := binary.Write(&tiff, binary.BigEndian, uint16(1)); err != nil {
		t.Fatalf("could not write IFD entry count: %v", err)
	}
	if err := binary.Write(&tiff, binary.BigEndian, uint16(0x0112)); err != nil {
		t.Fatalf("could not write orientation tag: %v", err)
	}
	if err := binary.Write(&tiff, binary.BigEndian, uint16(3)); err != nil {
		t.Fatalf("could not write orientation type: %v", err)
	}
	if err := binary.Write(&tiff, binary.BigEndian, uint32(1)); err != nil {
		t.Fatalf("could not write orientation count: %v", err)
	}
	if err := binary.Write(&tiff, binary.BigEndian, orientation); err != nil {
		t.Fatalf("could not write orientation value: %v", err)
	}
	if err := binary.Write(&tiff, binary.BigEndian, uint16(0)); err != nil {
		t.Fatalf("could not write orientation padding: %v", err)
	}
	if err := binary.Write(&tiff, binary.BigEndian, uint32(0)); err != nil {
		t.Fatalf("could not write next IFD pointer: %v", err)
	}

	payload := append([]byte("Exif\x00\x00"), tiff.Bytes()...)
	segmentLength := len(payload) + 2
	return append([]byte{0xFF, 0xE1, byte(segmentLength >> 8), byte(segmentLength)}, payload...)
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
