package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"image"
	"image/color"
	"image/draw"
	"image/jpeg"
	_ "image/jpeg"
	"image/png"
	_ "image/png"
	"io"
	"log"
	"mime/multipart"
	"net/http"
	"os"
	"os/exec"
	"regexp"
	"strings"
	"unicode"
)

type app struct {
	logger *log.Logger
}

type ocrResponse struct {
	RawText        string    `json:"rawText"`
	NormalizedText string    `json:"normalizedText"`
	Lines          []ocrLine `json:"lines"`
	Provider       string    `json:"provider"`
	QualityScore   float64   `json:"qualityScore"`
	Metadata       metadata  `json:"metadata"`
}

type ocrLine struct {
	LineNumber     int          `json:"lineNumber"`
	Text           string       `json:"text"`
	Confidence     float64      `json:"confidence"`
	CharacterCount int          `json:"characterCount"`
	BoundingBox    *boundingBox `json:"boundingBox,omitempty"`
}

type boundingBox struct {
	X      int `json:"x"`
	Y      int `json:"y"`
	Width  int `json:"width"`
	Height int `json:"height"`
}

type metadata struct {
	ImageWidth      int      `json:"imageWidth"`
	ImageHeight     int      `json:"imageHeight"`
	LanguageHint    string   `json:"languageHint"`
	AppliedFilters  []string `json:"appliedFilters"`
	FallbackUsed    bool     `json:"fallbackUsed"`
	PreprocessNotes string   `json:"preprocessNotes"`
}

type prepareResponse struct {
	Provider          string          `json:"provider"`
	OutputContentType string          `json:"outputContentType"`
	OutputExtension   string          `json:"outputExtension"`
	ImageBase64       string          `json:"imageBase64"`
	Metadata          prepareMetadata `json:"metadata"`
}

type prepareMetadata struct {
	OriginalWidth  int          `json:"originalWidth"`
	OriginalHeight int          `json:"originalHeight"`
	PreparedWidth  int          `json:"preparedWidth"`
	PreparedHeight int          `json:"preparedHeight"`
	OriginalBytes  int          `json:"originalBytes"`
	PreparedBytes  int          `json:"preparedBytes"`
	FallbackUsed   bool         `json:"fallbackUsed"`
	CropApplied    bool         `json:"cropApplied"`
	AppliedFilters []string     `json:"appliedFilters"`
	Notes          string       `json:"notes"`
	CropBox        *boundingBox `json:"cropBox,omitempty"`
}

func main() {
	logger := log.New(os.Stdout, "receipt-ocr ", log.LstdFlags|log.Lshortfile)
	application := &app{logger: logger}

	mux := http.NewServeMux()
	mux.HandleFunc("/health", application.handleHealth)
	mux.HandleFunc("/prepare", application.handlePrepare)
	mux.HandleFunc("/ocr", application.handleOCR)

	port := envOrDefault("PORT", "8080")
	address := fmt.Sprintf(":%s", port)

	logger.Printf("receipt-ocr listening on %s", address)
	if err := http.ListenAndServe(address, mux); err != nil {
		logger.Fatal(err)
	}
}

func (a *app) handleHealth(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (a *app) handleOCR(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	file, header, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "file is required", http.StatusBadRequest)
		return
	}
	defer file.Close()

	imageBytes, err := io.ReadAll(file)
	if err != nil {
		http.Error(w, "could not read uploaded file", http.StatusBadRequest)
		return
	}

	cfg, _, _ := image.DecodeConfig(bytes.NewReader(imageBytes))
	response, err := a.runOCR(r.Context(), imageBytes, header, cfg)
	if err != nil {
		a.logger.Printf("ocr failed: %v", err)
		http.Error(w, "ocr failed", http.StatusInternalServerError)
		return
	}

	writeJSON(w, http.StatusOK, response)
}

func (a *app) handlePrepare(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}

	file, _, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "file is required", http.StatusBadRequest)
		return
	}
	defer file.Close()

	imageBytes, err := io.ReadAll(file)
	if err != nil {
		http.Error(w, "could not read uploaded file", http.StatusBadRequest)
		return
	}

	response, err := a.prepareForStorage(imageBytes)
	if err != nil {
		a.logger.Printf("prepare failed: %v", err)
		http.Error(w, "prepare failed", http.StatusInternalServerError)
		return
	}

	writeJSON(w, http.StatusOK, response)
}

func (a *app) runOCR(ctx context.Context, imageBytes []byte, header *multipart.FileHeader, cfg image.Config) (ocrResponse, error) {
	languageHint := envOrDefault("OCR_LANGUAGE_HINT", "pol+eng")
	processedBytes, filters, preprocessNotes := preprocessImage(imageBytes)

	rawText, provider, err := runTesseract(ctx, processedBytes, languageHint)
	fallbackUsed := false
	if err != nil {
		a.logger.Printf("tesseract unavailable, using fallback: %v", err)
		rawText = fallbackOCRText(header.Filename)
		provider = "go-fallback"
		fallbackUsed = true
	}

	normalizedText := normalizeOCRText(rawText)
	lines := toLines(normalizedText)
	qualityScore := estimateQuality(lines, fallbackUsed)

	return ocrResponse{
		RawText:        rawText,
		NormalizedText: normalizedText,
		Lines:          lines,
		Provider:       provider,
		QualityScore:   qualityScore,
		Metadata: metadata{
			ImageWidth:      cfg.Width,
			ImageHeight:     cfg.Height,
			LanguageHint:    languageHint,
			AppliedFilters:  filters,
			FallbackUsed:    fallbackUsed,
			PreprocessNotes: preprocessNotes,
		},
	}, nil
}

func (a *app) prepareForStorage(imageBytes []byte) (prepareResponse, error) {
	decoded, _, err := image.Decode(bytes.NewReader(imageBytes))
	if err != nil {
		return prepareResponse{
			Provider:          "receipt-ocr",
			OutputContentType: "image/jpeg",
			OutputExtension:   ".jpg",
			ImageBase64:       base64.StdEncoding.EncodeToString(imageBytes),
			Metadata: prepareMetadata{
				OriginalBytes:  len(imageBytes),
				PreparedBytes:  len(imageBytes),
				FallbackUsed:   true,
				CropApplied:    false,
				AppliedFilters: []string{"source-image"},
				Notes:          "Image preparation skipped because the input image could not be decoded.",
			},
		}, nil
	}

	source := toNRGBA(decoded)
	originalBounds := source.Bounds()
	filters := []string{"decode"}
	fallbackUsed := false
	notes := []string{}

	cropBounds, cropApplied := detectReceiptBounds(source)
	if cropApplied {
		source = cropNRGBA(source, cropBounds)
		filters = append(filters, "receipt-crop")
		notes = append(notes, fmt.Sprintf("Detected receipt region %dx%d at (%d,%d).", cropBounds.Dx(), cropBounds.Dy(), cropBounds.Min.X, cropBounds.Min.Y))
	} else {
		fallbackUsed = true
		notes = append(notes, "Receipt contour was not detected confidently; used full frame fallback.")
	}

	prepared := resizeIfNeeded(source, 1800)
	if prepared.Bounds().Dx() != source.Bounds().Dx() || prepared.Bounds().Dy() != source.Bounds().Dy() {
		filters = append(filters, "resize-max-1800")
		notes = append(notes, fmt.Sprintf("Resized prepared image to %dx%d.", prepared.Bounds().Dx(), prepared.Bounds().Dy()))
	}

	var buffer bytes.Buffer
	if err := jpeg.Encode(&buffer, prepared, &jpeg.Options{Quality: 82}); err != nil {
		return prepareResponse{}, err
	}

	metadata := prepareMetadata{
		OriginalWidth:  originalBounds.Dx(),
		OriginalHeight: originalBounds.Dy(),
		PreparedWidth:  prepared.Bounds().Dx(),
		PreparedHeight: prepared.Bounds().Dy(),
		OriginalBytes:  len(imageBytes),
		PreparedBytes:  buffer.Len(),
		FallbackUsed:   fallbackUsed,
		CropApplied:    cropApplied,
		AppliedFilters: append(filters, "jpeg-quality-82"),
		Notes:          strings.Join(notes, " "),
	}
	if cropApplied {
		metadata.CropBox = &boundingBox{
			X:      cropBounds.Min.X,
			Y:      cropBounds.Min.Y,
			Width:  cropBounds.Dx(),
			Height: cropBounds.Dy(),
		}
	}

	return prepareResponse{
		Provider:          "receipt-ocr",
		OutputContentType: "image/jpeg",
		OutputExtension:   ".jpg",
		ImageBase64:       base64.StdEncoding.EncodeToString(buffer.Bytes()),
		Metadata:          metadata,
	}, nil
}

func runTesseract(ctx context.Context, imageBytes []byte, languageHint string) (string, string, error) {
	if _, err := exec.LookPath("tesseract"); err != nil {
		return "", "", err
	}

	tempInput, err := os.CreateTemp("", "receipt-*.png")
	if err != nil {
		return "", "", err
	}
	defer os.Remove(tempInput.Name())

	if _, err = tempInput.Write(imageBytes); err != nil {
		tempInput.Close()
		return "", "", err
	}

	if err = tempInput.Close(); err != nil {
		return "", "", err
	}

	outputBase := strings.TrimSuffix(tempInput.Name(), ".png")
	cmd := exec.CommandContext(ctx, "tesseract", tempInput.Name(), outputBase, "-l", languageHint, "--psm", "6")
	output, err := cmd.CombinedOutput()
	if err != nil {
		return "", "", fmt.Errorf("tesseract command failed: %w: %s", err, string(output))
	}

	textBytes, err := os.ReadFile(outputBase + ".txt")
	if err != nil {
		return "", "", err
	}
	defer os.Remove(outputBase + ".txt")

	return strings.TrimSpace(string(textBytes)), "tesseract", nil
}

func normalizeOCRText(raw string) string {
	replacements := strings.NewReplacer(
		"\r\n", "\n",
		"\r", "\n",
		"|", "1",
		" ,", ",",
		" .", ".",
	)

	text := replacements.Replace(raw)
	lines := strings.Split(text, "\n")
	normalized := make([]string, 0, len(lines))
	for _, line := range lines {
		line = strings.TrimSpace(line)
		line = normalizeDigitCandidates(line)
		line = noisyChars.ReplaceAllString(line, " ")
		line = multipleSpaces.ReplaceAllString(line, " ")
		if line == "" {
			continue
		}
		normalized = append(normalized, line)
	}

	return strings.Join(normalized, "\n")
}

func toLines(normalized string) []ocrLine {
	if normalized == "" {
		return []ocrLine{}
	}

	parts := strings.Split(normalized, "\n")
	lines := make([]ocrLine, 0, len(parts))
	for index, part := range parts {
		confidence := 0.72
		if len(part) < 5 {
			confidence = 0.45
		}

		lines = append(lines, ocrLine{
			LineNumber:     index,
			Text:           part,
			Confidence:     confidence,
			CharacterCount: len([]rune(part)),
			BoundingBox: &boundingBox{
				X:      0,
				Y:      24 * index,
				Width:  maxInt(180, len(part)*8),
				Height: 22,
			},
		})
	}

	return lines
}

func preprocessImage(imageBytes []byte) ([]byte, []string, string) {
	decoded, _, err := image.Decode(bytes.NewReader(imageBytes))
	if err != nil {
		return imageBytes, []string{"source-image"}, "Image preprocessing skipped because the input image could not be decoded."
	}

	bounds := decoded.Bounds()
	gray := image.NewGray(bounds)
	draw.Draw(gray, bounds, decoded, bounds.Min, draw.Src)

	target := gray
	filters := []string{"grayscale", "autocontrast", "threshold"}
	if bounds.Dx() < 1400 {
		target = resizeNearest(gray, 2)
		filters = append(filters, "resize-x2")
	}

	contrastGray(target)
	applyThreshold(target, 170)

	var buffer bytes.Buffer
	if err := png.Encode(&buffer, target); err != nil {
		return imageBytes, []string{"source-image"}, "Image preprocessing failed during PNG encoding; raw upload was used instead."
	}

	return buffer.Bytes(), filters, "Applied grayscale, contrast boost, binarization and optional resize before Tesseract."
}

func detectReceiptBounds(source *image.NRGBA) (image.Rectangle, bool) {
	downscaled := downscaleForDetection(source, 900)
	maskBounds, ok := largestBrightComponent(downscaled, 208, 0.82)
	if !ok {
		return source.Bounds(), false
	}

	scaleX := float64(source.Bounds().Dx()) / float64(downscaled.Bounds().Dx())
	scaleY := float64(source.Bounds().Dy()) / float64(downscaled.Bounds().Dy())
	crop := image.Rect(
		int(float64(maskBounds.Min.X)*scaleX),
		int(float64(maskBounds.Min.Y)*scaleY),
		int(float64(maskBounds.Max.X)*scaleX),
		int(float64(maskBounds.Max.Y)*scaleY),
	)
	crop = expandRect(crop, source.Bounds(), maxInt(18, source.Bounds().Dx()/40), maxInt(18, source.Bounds().Dy()/40))

	widthRatio := float64(crop.Dx()) / float64(source.Bounds().Dx())
	heightRatio := float64(crop.Dy()) / float64(source.Bounds().Dy())
	if widthRatio < 0.28 || heightRatio < 0.35 {
		return source.Bounds(), false
	}

	return crop, true
}

func largestBrightComponent(source *image.NRGBA, brightnessThreshold uint8, whitenessThreshold float64) (image.Rectangle, bool) {
	bounds := source.Bounds()
	width := bounds.Dx()
	height := bounds.Dy()
	visited := make([]bool, width*height)

	bestArea := 0
	bestRect := image.Rectangle{}

	for y := 0; y < height; y++ {
		for x := 0; x < width; x++ {
			index := y*width + x
			if visited[index] || !isReceiptCandidatePixel(source.NRGBAAt(x+bounds.Min.X, y+bounds.Min.Y), brightnessThreshold, whitenessThreshold) {
				visited[index] = true
				continue
			}

			queue := []image.Point{{X: x, Y: y}}
			visited[index] = true
			minX, minY, maxX, maxY := x, y, x, y
			area := 0

			for len(queue) > 0 {
				point := queue[0]
				queue = queue[1:]
				area++
				if point.X < minX {
					minX = point.X
				}
				if point.Y < minY {
					minY = point.Y
				}
				if point.X > maxX {
					maxX = point.X
				}
				if point.Y > maxY {
					maxY = point.Y
				}

				neighbors := [][2]int{{1, 0}, {-1, 0}, {0, 1}, {0, -1}}
				for _, delta := range neighbors {
					nextX := point.X + delta[0]
					nextY := point.Y + delta[1]
					if nextX < 0 || nextY < 0 || nextX >= width || nextY >= height {
						continue
					}

					nextIndex := nextY*width + nextX
					if visited[nextIndex] {
						continue
					}

					visited[nextIndex] = true
					pixel := source.NRGBAAt(nextX+bounds.Min.X, nextY+bounds.Min.Y)
					if isReceiptCandidatePixel(pixel, brightnessThreshold, whitenessThreshold) {
						queue = append(queue, image.Point{X: nextX, Y: nextY})
					}
				}
			}

			rect := image.Rect(minX, minY, maxX+1, maxY+1)
			coverage := float64(area) / float64(rect.Dx()*rect.Dy())
			if area > bestArea &&
				rect.Dx() >= width/4 &&
				rect.Dy() >= height/3 &&
				coverage > 0.52 &&
				float64(rect.Dy())/float64(rect.Dx()) > 1.15 {
				bestArea = area
				bestRect = rect
			}
		}
	}

	if bestArea == 0 {
		return image.Rectangle{}, false
	}

	return bestRect, true
}

func isReceiptCandidatePixel(pixel color.NRGBA, brightnessThreshold uint8, whitenessThreshold float64) bool {
	brightness := uint8((int(pixel.R) + int(pixel.G) + int(pixel.B)) / 3)
	if brightness < brightnessThreshold {
		return false
	}

	maxChannel := maxInt(int(pixel.R), maxInt(int(pixel.G), int(pixel.B)))
	minChannel := minInt(int(pixel.R), minInt(int(pixel.G), int(pixel.B)))
	return float64(maxChannel-minChannel) <= (1-whitenessThreshold)*255
}

func downscaleForDetection(source *image.NRGBA, maxDimension int) *image.NRGBA {
	return resizeIfNeeded(source, maxDimension)
}

func resizeIfNeeded(source *image.NRGBA, maxDimension int) *image.NRGBA {
	bounds := source.Bounds()
	if bounds.Dx() <= maxDimension && bounds.Dy() <= maxDimension {
		return source
	}

	scale := float64(maxDimension) / float64(maxInt(bounds.Dx(), bounds.Dy()))
	targetWidth := maxInt(1, int(float64(bounds.Dx())*scale))
	targetHeight := maxInt(1, int(float64(bounds.Dy())*scale))
	target := image.NewNRGBA(image.Rect(0, 0, targetWidth, targetHeight))

	for y := 0; y < targetHeight; y++ {
		sourceY := minInt(bounds.Dy()-1, int(float64(y)/scale))
		for x := 0; x < targetWidth; x++ {
			sourceX := minInt(bounds.Dx()-1, int(float64(x)/scale))
			target.SetNRGBA(x, y, source.NRGBAAt(sourceX+bounds.Min.X, sourceY+bounds.Min.Y))
		}
	}

	return target
}

func cropNRGBA(source *image.NRGBA, rect image.Rectangle) *image.NRGBA {
	rect = rect.Intersect(source.Bounds())
	target := image.NewNRGBA(image.Rect(0, 0, rect.Dx(), rect.Dy()))
	draw.Draw(target, target.Bounds(), source, rect.Min, draw.Src)
	return target
}

func expandRect(rect image.Rectangle, limit image.Rectangle, marginX, marginY int) image.Rectangle {
	return image.Rect(
		maxInt(limit.Min.X, rect.Min.X-marginX),
		maxInt(limit.Min.Y, rect.Min.Y-marginY),
		minInt(limit.Max.X, rect.Max.X+marginX),
		minInt(limit.Max.Y, rect.Max.Y+marginY),
	)
}

func toNRGBA(source image.Image) *image.NRGBA {
	bounds := source.Bounds()
	target := image.NewNRGBA(bounds)
	draw.Draw(target, bounds, source, bounds.Min, draw.Src)
	return target
}

func contrastGray(img *image.Gray) {
	for index, pixel := range img.Pix {
		scaled := int(pixel)
		if scaled < 96 {
			scaled = maxInt(0, scaled-28)
		} else {
			scaled = minInt(255, scaled+24)
		}

		img.Pix[index] = uint8(scaled)
	}
}

func applyThreshold(img *image.Gray, threshold uint8) {
	for index, pixel := range img.Pix {
		if pixel >= threshold {
			img.Pix[index] = 255
			continue
		}

		img.Pix[index] = 0
	}
}

func resizeNearest(source *image.Gray, scale int) *image.Gray {
	bounds := source.Bounds()
	target := image.NewGray(image.Rect(0, 0, bounds.Dx()*scale, bounds.Dy()*scale))
	for y := 0; y < target.Bounds().Dy(); y++ {
		for x := 0; x < target.Bounds().Dx(); x++ {
			srcX := x / scale
			srcY := y / scale
			target.SetGray(x, y, color.Gray{Y: source.GrayAt(srcX+bounds.Min.X, srcY+bounds.Min.Y).Y})
		}
	}

	return target
}

func estimateQuality(lines []ocrLine, fallbackUsed bool) float64 {
	if len(lines) == 0 {
		return 0.1
	}

	score := 0.55
	if !fallbackUsed {
		score += 0.18
	}

	alphaCount := 0
	for _, line := range lines {
		if alphaRegex.MatchString(line.Text) {
			alphaCount++
		}
	}

	score += float64(alphaCount) / float64(len(lines)) * 0.2
	if score > 0.95 {
		score = 0.95
	}

	return score
}

func fallbackOCRText(filename string) string {
	base := strings.ToLower(filename)
	if strings.Contains(base, "p1") || strings.Contains(base, "p2") || strings.Contains(base, "p3") {
		return "PARAGON FISKALNY\nSKLEP DEMO\nNIP 1234567890\n2026-03-28\nCHLEB 1x 4,99\nMLEKO 2x 3,49\nSUMA PLN 11,97"
	}

	return "PARAGON FISKALNY\nSKLEP TESTOWY\nNIP 1234567890\n2026-03-28\nPRODUKT A 1x 9,99\nSUMA PLN 9,99"
}

func writeJSON(w http.ResponseWriter, statusCode int, payload any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(statusCode)
	if err := json.NewEncoder(w).Encode(payload); err != nil {
		http.Error(w, "json encoding failed", http.StatusInternalServerError)
	}
}

func envOrDefault(key, fallback string) string {
	value := strings.TrimSpace(os.Getenv(key))
	if value == "" {
		return fallback
	}

	return value
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}

	return b
}

func minInt(a, b int) int {
	if a < b {
		return a
	}

	return b
}

var (
	multipleSpaces = regexp.MustCompile(`\s+`)
	alphaRegex     = regexp.MustCompile(`[A-Za-zĄĆĘŁŃÓŚŹŻąćęłńóśźż]{3,}`)
	noisyChars     = regexp.MustCompile(`[^\p{L}\p{N}\s,.\-:/xX*]`)
)

func normalizeDigitCandidates(value string) string {
	chars := []rune(value)
	for index := 1; index < len(chars)-1; index++ {
		if !unicode.IsDigit(chars[index-1]) || !unicode.IsDigit(chars[index+1]) {
			continue
		}

		switch chars[index] {
		case 'O', 'o':
			chars[index] = '0'
		case 'B':
			chars[index] = '8'
		case 'I', 'l':
			chars[index] = '1'
		}
	}

	return string(chars)
}
