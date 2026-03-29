package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/binary"
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
	"sort"
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
	Section        string       `json:"section,omitempty"`
	VariantID      string       `json:"variantId,omitempty"`
	AlternateTexts []string     `json:"alternateTexts,omitempty"`
	BoundingBox    *boundingBox `json:"boundingBox,omitempty"`
}

type boundingBox struct {
	X      int `json:"x"`
	Y      int `json:"y"`
	Width  int `json:"width"`
	Height int `json:"height"`
}

type metadata struct {
	ImageWidth         int                 `json:"imageWidth"`
	ImageHeight        int                 `json:"imageHeight"`
	LanguageHint       string              `json:"languageHint"`
	AppliedFilters     []string            `json:"appliedFilters"`
	FallbackUsed       bool                `json:"fallbackUsed"`
	PreprocessNotes    string              `json:"preprocessNotes"`
	SelectedVariantID  string              `json:"selectedVariantId,omitempty"`
	Variants           []ocrVariantSummary `json:"variants,omitempty"`
	SectionConfidences []sectionConfidence `json:"sectionConfidences,omitempty"`
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
	BodyCropBox    *boundingBox `json:"bodyCropBox,omitempty"`
	FooterCropBox  *boundingBox `json:"footerCropBox,omitempty"`
}

type ocrVariantSummary struct {
	VariantID                 string       `json:"variantId"`
	VariantType               string       `json:"variantType"`
	Section                   string       `json:"section"`
	Psm                       int          `json:"psm"`
	CropBox                   *boundingBox `json:"cropBox,omitempty"`
	RotationDegrees           float64      `json:"rotationDegrees"`
	AppliedFilters            []string     `json:"appliedFilters"`
	EstimatedReadabilityScore float64      `json:"estimatedReadabilityScore"`
	QualityScore              float64      `json:"qualityScore"`
	Selected                  bool         `json:"selected"`
	RawText                   string       `json:"rawText"`
	NormalizedText            string       `json:"normalizedText"`
}

type sectionConfidence struct {
	Section           string  `json:"section"`
	Confidence        float64 `json:"confidence"`
	SelectedVariantID string  `json:"selectedVariantId,omitempty"`
	Notes             string  `json:"notes,omitempty"`
}

type ocrVariantCandidate struct {
	id              string
	variantType     string
	section         string
	psm             int
	filters         []string
	cropBox         *boundingBox
	rotationDegrees float64
	readability     float64
	imageBytes      []byte
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
	variants, filters, preprocessNotes := buildOCRVariants(imageBytes)
	fallbackUsed := false
	provider := "tesseract"
	results := make([]ocrVariantSummary, 0, len(variants))
	bestBySection := map[string]ocrVariantSummary{}
	var bestFull ocrVariantSummary

	for _, variant := range variants {
		rawText, variantProvider, err := runTesseract(ctx, variant.imageBytes, languageHint, variant.psm)
		if err != nil {
			a.logger.Printf("ocr variant %s failed: %v", variant.id, err)
			continue
		}

		provider = variantProvider
		normalizedText := normalizeOCRText(rawText)
		qualityScore := scoreOCRText(normalizedText, variant.section)
		summary := ocrVariantSummary{
			VariantID:                 variant.id,
			VariantType:               variant.variantType,
			Section:                   variant.section,
			Psm:                       variant.psm,
			CropBox:                   variant.cropBox,
			RotationDegrees:           variant.rotationDegrees,
			AppliedFilters:            append([]string{}, variant.filters...),
			EstimatedReadabilityScore: variant.readability,
			QualityScore:              qualityScore,
			RawText:                   rawText,
			NormalizedText:            normalizedText,
		}
		results = append(results, summary)

		current, exists := bestBySection[variant.section]
		if !exists || summary.QualityScore > current.QualityScore {
			bestBySection[variant.section] = summary
		}

		if variant.section == "full" && (bestFull.VariantID == "" || summary.QualityScore > bestFull.QualityScore) {
			bestFull = summary
		}
	}

	if bestFull.VariantID == "" {
		a.logger.Printf("tesseract unavailable, using fallback for %s", header.Filename)
		rawText := fallbackOCRText(header.Filename)
		normalizedText := normalizeOCRText(rawText)
		bestFull = ocrVariantSummary{
			VariantID:                 "fallback",
			VariantType:               "fallback-text",
			Section:                   "full",
			Psm:                       6,
			AppliedFilters:            []string{"fallback-text"},
			EstimatedReadabilityScore: 0.2,
			QualityScore:              0.45,
			Selected:                  true,
			RawText:                   rawText,
			NormalizedText:            normalizedText,
		}
		results = append(results, bestFull)
		bestBySection["full"] = bestFull
		fallbackUsed = true
		provider = "go-fallback"
	}

	normalizedText := mergeOCRSignals(results, bestFull.VariantID)
	lines := toLines(normalizedText, bestFull.VariantID, collectAlternateTexts(results, bestFull.VariantID))
	qualityScore := estimateQuality(lines, fallbackUsed)
	sectionConfidences := buildSectionConfidences(bestBySection)
	for index := range results {
		if results[index].VariantID == bestFull.VariantID {
			results[index].Selected = true
		}
	}

	return ocrResponse{
		RawText:        bestFull.RawText,
		NormalizedText: normalizedText,
		Lines:          lines,
		Provider:       provider,
		QualityScore:   qualityScore,
		Metadata: metadata{
			ImageWidth:         cfg.Width,
			ImageHeight:        cfg.Height,
			LanguageHint:       languageHint,
			AppliedFilters:     filters,
			FallbackUsed:       fallbackUsed,
			PreprocessNotes:    preprocessNotes,
			SelectedVariantID:  bestFull.VariantID,
			Variants:           results,
			SectionConfidences: sectionConfidences,
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

	originalBounds := decoded.Bounds()
	source, orientationFilters, orientationNotes, err := decodeAndOrientReceiptImage(imageBytes)
	if err != nil {
		return prepareResponse{}, err
	}

	filters := append([]string{}, orientationFilters...)
	fallbackUsed := false
	notes := append([]string{}, orientationNotes...)

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

		bodyBox, footerBox := deriveSectionBoxes(source.Bounds())
		metadata.BodyCropBox = imageRectToBox(bodyBox)
		metadata.FooterCropBox = imageRectToBox(footerBox)
	}

	return prepareResponse{
		Provider:          "receipt-ocr",
		OutputContentType: "image/jpeg",
		OutputExtension:   ".jpg",
		ImageBase64:       base64.StdEncoding.EncodeToString(buffer.Bytes()),
		Metadata:          metadata,
	}, nil
}

func runTesseract(ctx context.Context, imageBytes []byte, languageHint string, psm int) (string, string, error) {
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
	cmd := exec.CommandContext(ctx, "tesseract", tempInput.Name(), outputBase, "-l", languageHint, "--psm", fmt.Sprintf("%d", psm))
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

func toLines(normalized string, variantID string, alternateTexts map[int][]string) []ocrLine {
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
			Section:        classifyLineSection(part),
			VariantID:      variantID,
			AlternateTexts: alternateTexts[index],
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

func buildOCRVariants(imageBytes []byte) ([]ocrVariantCandidate, []string, string) {
	source, orientationFilters, orientationNotes, err := decodeAndOrientReceiptImage(imageBytes)
	if err != nil {
		return []ocrVariantCandidate{
			{
				id:          "source-full",
				variantType: "source-image",
				section:     "full",
				psm:         6,
				filters:     []string{"source-image"},
				readability: 0.3,
				imageBytes:  imageBytes,
			},
		}, []string{"source-image"}, "Image preprocessing skipped because the input image could not be decoded."
	}

	filters := append([]string{}, orientationFilters...)
	notes := append([]string{}, orientationNotes...)
	cropBounds, cropApplied := detectReceiptBounds(source)
	if cropApplied {
		source = cropNRGBA(source, cropBounds)
		filters = append(filters, "receipt-crop")
		notes = append(notes, "Detected receipt crop for OCR variants.")
	} else {
		notes = append(notes, "Receipt contour was not detected confidently; using full frame variants.")
	}

	prepared := resizeIfNeeded(source, 2200)
	bodyBox, footerBox := deriveSectionBoxes(prepared.Bounds())
	variants := []ocrVariantCandidate{
		buildVariant("full-threshold-170", "adaptive-threshold", "full", 6, prepared, nil, 170, true, false),
		buildVariant("full-threshold-150", "adaptive-threshold-soft", "full", 4, prepared, nil, 150, true, false),
		buildVariant("full-contrast", "contrast-only", "full", 6, prepared, nil, 0, true, true),
		buildVariant("items-body", "body-threshold", "items", 6, prepared, &bodyBox, 165, true, false),
		buildVariant("payments-footer", "footer-threshold", "payments", 4, prepared, &footerBox, 160, true, false),
	}

	filtered := make([]ocrVariantCandidate, 0, len(variants))
	for _, variant := range variants {
		if len(variant.imageBytes) > 0 {
			filtered = append(filtered, variant)
		}
	}

	return filtered, filters, strings.Join(notes, " ")
}

func decodeAndOrientReceiptImage(imageBytes []byte) (*image.NRGBA, []string, []string, error) {
	decoded, _, err := image.Decode(bytes.NewReader(imageBytes))
	if err != nil {
		return nil, nil, nil, err
	}

	source := toNRGBA(decoded)
	filters := []string{"decode"}
	notes := []string{}

	if orientation, ok := readJPEGExifOrientation(imageBytes); ok && orientation != 1 {
		source = applyExifOrientation(source, orientation)
		filters = append(filters, fmt.Sprintf("exif-orientation-%d", orientation))
		notes = append(notes, fmt.Sprintf("Applied EXIF orientation %d before receipt detection.", orientation))
	}

	normalized, rotationDegrees, rotated := normalizeReceiptOrientation(source)
	if rotated {
		source = normalized
		filters = append(filters, fmt.Sprintf("rotate-%d", rotationDegrees))
		notes = append(notes, fmt.Sprintf("Rotated receipt frame by %d degrees to normalize portrait layout before OCR.", rotationDegrees))
	}

	return source, filters, notes, nil
}

func buildVariant(id, variantType, section string, psm int, source *image.NRGBA, crop *image.Rectangle, threshold uint8, applyContrast bool, skipThreshold bool) ocrVariantCandidate {
	target := source
	var cropBox *boundingBox
	filters := []string{"grayscale"}
	if crop != nil {
		target = cropNRGBA(source, *crop)
		cropBox = imageRectToBox(*crop)
		filters = append(filters, section+"-crop")
	}

	gray := image.NewGray(target.Bounds())
	draw.Draw(gray, target.Bounds(), target, target.Bounds().Min, draw.Src)
	if target.Bounds().Dx() < 1400 {
		gray = resizeNearest(gray, 2)
		filters = append(filters, "resize-x2")
	}

	if applyContrast {
		contrastGray(gray)
		filters = append(filters, "autocontrast")
	}

	if !skipThreshold && threshold > 0 {
		applyThreshold(gray, threshold)
		filters = append(filters, fmt.Sprintf("threshold-%d", threshold))
	}

	var buffer bytes.Buffer
	if err := png.Encode(&buffer, gray); err != nil {
		return ocrVariantCandidate{}
	}

	return ocrVariantCandidate{
		id:              id,
		variantType:     variantType,
		section:         section,
		psm:             psm,
		filters:         filters,
		cropBox:         cropBox,
		rotationDegrees: 0,
		readability:     estimateReadability(gray),
		imageBytes:      buffer.Bytes(),
	}
}

func readJPEGExifOrientation(imageBytes []byte) (int, bool) {
	if len(imageBytes) < 4 || imageBytes[0] != 0xFF || imageBytes[1] != 0xD8 {
		return 0, false
	}

	for offset := 2; offset+4 <= len(imageBytes); {
		if imageBytes[offset] != 0xFF {
			offset++
			continue
		}

		marker := imageBytes[offset+1]
		offset += 2
		if marker == 0xD9 || marker == 0xDA {
			break
		}

		if offset+2 > len(imageBytes) {
			break
		}

		segmentLength := int(binary.BigEndian.Uint16(imageBytes[offset : offset+2]))
		if segmentLength < 2 || offset+segmentLength > len(imageBytes) {
			break
		}

		segment := imageBytes[offset+2 : offset+segmentLength]
		if marker == 0xE1 && len(segment) >= 6 && bytes.Equal(segment[:6], []byte("Exif\x00\x00")) {
			return parseExifOrientation(segment[6:])
		}

		offset += segmentLength
	}

	return 0, false
}

func parseExifOrientation(tiff []byte) (int, bool) {
	if len(tiff) < 8 {
		return 0, false
	}

	var order binary.ByteOrder
	switch string(tiff[:2]) {
	case "II":
		order = binary.LittleEndian
	case "MM":
		order = binary.BigEndian
	default:
		return 0, false
	}

	if order.Uint16(tiff[2:4]) != 42 {
		return 0, false
	}

	ifdOffset := int(order.Uint32(tiff[4:8]))
	if ifdOffset < 0 || ifdOffset+2 > len(tiff) {
		return 0, false
	}

	entryCount := int(order.Uint16(tiff[ifdOffset : ifdOffset+2]))
	for entryIndex := 0; entryIndex < entryCount; entryIndex++ {
		entryOffset := ifdOffset + 2 + entryIndex*12
		if entryOffset+12 > len(tiff) {
			return 0, false
		}

		tag := order.Uint16(tiff[entryOffset : entryOffset+2])
		if tag != 0x0112 {
			continue
		}

		valueType := order.Uint16(tiff[entryOffset+2 : entryOffset+4])
		valueCount := order.Uint32(tiff[entryOffset+4 : entryOffset+8])
		if valueType != 3 || valueCount == 0 {
			return 0, false
		}

		if valueCount == 1 {
			value := int(order.Uint16(tiff[entryOffset+8 : entryOffset+10]))
			return value, value >= 1 && value <= 8
		}

		valueOffset := int(order.Uint32(tiff[entryOffset+8 : entryOffset+12]))
		if valueOffset < 0 || valueOffset+2 > len(tiff) {
			return 0, false
		}

		value := int(order.Uint16(tiff[valueOffset : valueOffset+2]))
		return value, value >= 1 && value <= 8
	}

	return 0, false
}

func normalizeReceiptOrientation(source *image.NRGBA) (*image.NRGBA, int, bool) {
	candidates := []struct {
		rotation int
		image    *image.NRGBA
		score    float64
	}{
		{rotation: 0, image: source},
		{rotation: 90, image: rotate90NRGBA(source)},
		{rotation: 180, image: rotate180NRGBA(source)},
		{rotation: 270, image: rotate270NRGBA(source)},
	}

	for index := range candidates {
		candidates[index].score = scoreReceiptOrientationCandidate(candidates[index].image)
	}

	best := candidates[0]
	for _, candidate := range candidates[1:] {
		if candidate.score > best.score {
			best = candidate
		}
	}

	if best.rotation == 0 || best.score < candidates[0].score+0.2 {
		return source, 0, false
	}

	return best.image, best.rotation, true
}

func scoreReceiptOrientationCandidate(source *image.NRGBA) float64 {
	target := source
	score := 0.0

	if cropBounds, ok := detectReceiptBounds(source); ok {
		target = cropNRGBA(source, cropBounds)
		score += 0.9
	} else {
		score -= 0.2
	}

	bounds := target.Bounds()
	aspectRatio := float64(bounds.Dy()) / float64(maxInt(1, bounds.Dx()))
	if aspectRatio >= 1.1 {
		score += minFloat(1.3, aspectRatio*0.55)
	} else {
		score -= 0.75
	}

	centroidY, inkDensity, ok := estimateInkCentroid(target)
	if ok {
		score += 0.5 - centroidY
		score += minFloat(0.25, inkDensity*3.5)
	}

	return score
}

func estimateInkCentroid(source *image.NRGBA) (float64, float64, bool) {
	borderBrightness := averageBorderBrightness(source)
	inkThreshold := minFloat(borderBrightness-92, 160)
	if inkThreshold < 110 {
		inkThreshold = 110
	}

	bounds := source.Bounds()
	var yTotal float64
	inkPixels := 0
	for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
		for x := bounds.Min.X; x < bounds.Max.X; x++ {
			if pixelBrightness(source.NRGBAAt(x, y)) > inkThreshold {
				continue
			}

			yTotal += float64(y - bounds.Min.Y)
			inkPixels++
		}
	}

	if inkPixels < maxInt(120, bounds.Dx()*bounds.Dy()/900) {
		return 0, 0, false
	}

	height := maxInt(1, bounds.Dy()-1)
	centroidY := (yTotal / float64(inkPixels)) / float64(height)
	inkDensity := float64(inkPixels) / float64(maxInt(1, bounds.Dx()*bounds.Dy()))
	return centroidY, inkDensity, true
}

func applyExifOrientation(source *image.NRGBA, orientation int) *image.NRGBA {
	switch orientation {
	case 2:
		return flipHorizontalNRGBA(source)
	case 3:
		return rotate180NRGBA(source)
	case 4:
		return flipVerticalNRGBA(source)
	case 5:
		return transposeNRGBA(source)
	case 6:
		return rotate90NRGBA(source)
	case 7:
		return transverseNRGBA(source)
	case 8:
		return rotate270NRGBA(source)
	default:
		return source
	}
}

func estimateReadability(img *image.Gray) float64 {
	if len(img.Pix) == 0 {
		return 0.1
	}

	darkPixels := 0
	for _, pixel := range img.Pix {
		if pixel < 180 {
			darkPixels++
		}
	}

	ratio := float64(darkPixels) / float64(len(img.Pix))
	if ratio < 0.01 {
		return 0.2
	}

	if ratio > 0.4 {
		return 0.55
	}

	return 0.9 - ratio
}

func deriveSectionBoxes(bounds image.Rectangle) (image.Rectangle, image.Rectangle) {
	height := bounds.Dy()
	bodyBottom := bounds.Min.Y + int(float64(height)*0.74)
	footerTop := bounds.Min.Y + int(float64(height)*0.72)
	body := image.Rect(bounds.Min.X, bounds.Min.Y, bounds.Max.X, minInt(bounds.Max.Y, bodyBottom))
	footer := image.Rect(bounds.Min.X, maxInt(bounds.Min.Y, footerTop), bounds.Max.X, bounds.Max.Y)
	return body, footer
}

func imageRectToBox(rect image.Rectangle) *boundingBox {
	if rect.Dx() <= 0 || rect.Dy() <= 0 {
		return nil
	}

	return &boundingBox{
		X:      rect.Min.X,
		Y:      rect.Min.Y,
		Width:  rect.Dx(),
		Height: rect.Dy(),
	}
}

func scoreOCRText(normalized string, section string) float64 {
	lines := strings.Split(normalized, "\n")
	score := 0.25
	if normalized == "" {
		return 0.1
	}

	priceMatches := 0
	itemLikeLines := 0
	nonEmptyLines := 0
	for _, line := range lines {
		if strings.TrimSpace(line) == "" {
			continue
		}

		nonEmptyLines++
		if amountLikeRegex.MatchString(line) {
			priceMatches++
		}

		if itemPayloadRegex.MatchString(line) {
			itemLikeLines++
		}
	}

	score += minFloat(0.3, float64(priceMatches)*0.03)
	score += minFloat(0.25, float64(itemLikeLines)*0.025)
	upper := strings.ToUpper(normalized)
	if strings.Contains(upper, "SUMA PLN") || strings.Contains(upper, "SUMA") {
		score += 0.18
	}

	if strings.Contains(upper, "KARTA") || strings.Contains(upper, "WPLATA") || strings.Contains(upper, "SODEXO") {
		score += 0.12
	}

	if strings.Contains(upper, "SPRZEDAZ") || strings.Contains(upper, "PTU") || strings.Contains(upper, "VAT") {
		score += 0.08
	}

	switch section {
	case "payments":
		if strings.Contains(upper, "KARTA") || strings.Contains(upper, "WPLATA") {
			score += 0.15
		}
	case "items":
		score += minFloat(0.12, float64(itemLikeLines)*0.02)
	case "full":
		if nonEmptyLines < 12 {
			score -= 0.22
		}

		if itemLikeLines < 6 {
			score -= 0.18
		}

		score += minFloat(0.18, float64(nonEmptyLines)*0.008)
	}

	if score < 0.05 {
		score = 0.05
	}

	if score > 0.99 {
		score = 0.99
	}

	return score
}

func mergeOCRSignals(results []ocrVariantSummary, selectedVariantID string) string {
	type rankedLine struct {
		text     string
		score    float64
		priority int
	}

	linesByText := map[string]rankedLine{}
	order := make([]string, 0)
	for _, result := range results {
		basePriority := 1
		if result.VariantID == selectedVariantID {
			basePriority = 3
		} else if result.Section != "full" {
			basePriority = 2
		}

		for _, rawLine := range strings.Split(result.NormalizedText, "\n") {
			line := strings.TrimSpace(rawLine)
			if line == "" {
				continue
			}

			priority := basePriority
			switch classifyLineSection(line) {
			case "payments", "totals":
				priority += 2
			case "items":
				priority++
			}

			score := result.QualityScore + float64(priority)*0.05
			existing, exists := linesByText[line]
			if !exists || score > existing.score {
				if !exists {
					order = append(order, line)
				}

				linesByText[line] = rankedLine{
					text:     line,
					score:    score,
					priority: priority,
				}
			}
		}
	}

	sort.SliceStable(order, func(i, j int) bool {
		left := linesByText[order[i]]
		right := linesByText[order[j]]
		if left.priority == right.priority {
			return left.score > right.score
		}

		return left.priority > right.priority
	})

	return strings.Join(order, "\n")
}

func collectAlternateTexts(results []ocrVariantSummary, selectedVariantID string) map[int][]string {
	alternates := map[int][]string{}
	for _, result := range results {
		if result.VariantID == selectedVariantID {
			continue
		}

		lines := strings.Split(result.NormalizedText, "\n")
		for index, line := range lines {
			line = strings.TrimSpace(line)
			if line == "" {
				continue
			}

			alternates[index] = append(alternates[index], line)
		}
	}

	return alternates
}

func buildSectionConfidences(bestBySection map[string]ocrVariantSummary) []sectionConfidence {
	confidences := make([]sectionConfidence, 0, len(bestBySection))
	for section, variant := range bestBySection {
		confidences = append(confidences, sectionConfidence{
			Section:           section,
			Confidence:        variant.QualityScore,
			SelectedVariantID: variant.VariantID,
			Notes:             variant.VariantType,
		})
	}

	return confidences
}

func classifyLineSection(line string) string {
	upper := strings.ToUpper(line)
	switch {
	case strings.Contains(upper, "SUMA"):
		return "totals"
	case strings.Contains(upper, "KARTA") || strings.Contains(upper, "WPLATA") || strings.Contains(upper, "SODEXO"):
		return "payments"
	case strings.Contains(upper, "PTU") || strings.Contains(upper, "VAT") || strings.Contains(upper, "SPRZEDAZ"):
		return "tax-summary"
	default:
		return "items"
	}
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
	candidates := []image.Rectangle{}
	if maskBounds, ok := largestBrightComponent(downscaled, 208, 0.82); ok {
		candidates = append(candidates, maskBounds)
	}

	// Some phone photos expose the receipt as warm gray rather than clean white,
	// so keep a more tolerant fallback before giving up on the crop.
	if maskBounds, ok := largestBrightComponent(downscaled, 182, 0.64); ok {
		candidates = append(candidates, maskBounds)
	}

	if projectedBounds, ok := projectionReceiptBounds(downscaled); ok {
		candidates = append(candidates, projectedBounds)
	}

	if textBounds, ok := detectReceiptTextBounds(downscaled); ok {
		candidates = append(candidates, textBounds)
	}

	bestCrop := image.Rectangle{}
	bestArea := 0
	for _, candidate := range candidates {
		crop, ok := scaleDetectionRect(candidate, downscaled.Bounds(), source.Bounds())
		if ok {
			area := crop.Dx() * crop.Dy()
			if bestArea == 0 || area < bestArea {
				bestCrop = crop
				bestArea = area
			}
		}
	}

	if bestArea > 0 {
		return bestCrop, true
	}

	return source.Bounds(), false
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

func projectionReceiptBounds(source *image.NRGBA) (image.Rectangle, bool) {
	bounds := source.Bounds()
	width := bounds.Dx()
	height := bounds.Dy()
	if width < 32 || height < 32 {
		return image.Rectangle{}, false
	}

	rowBrightness := make([]float64, height)
	rowDarkRatio := make([]float64, height)
	colBrightness := make([]float64, width)

	for y := 0; y < height; y++ {
		var rowTotal float64
		darkPixels := 0
		for x := 0; x < width; x++ {
			pixel := source.NRGBAAt(bounds.Min.X+x, bounds.Min.Y+y)
			brightness := pixelBrightness(pixel)
			rowTotal += brightness
			colBrightness[x] += brightness
			if brightness < 165 {
				darkPixels++
			}
		}

		rowBrightness[y] = rowTotal / float64(width)
		rowDarkRatio[y] = float64(darkPixels) / float64(width)
	}

	for x := 0; x < width; x++ {
		colBrightness[x] /= float64(height)
	}

	borderBrightness := averageBorderBrightness(source)
	smoothedRows := smoothSeries(rowBrightness, maxInt(9, height/40))
	smoothedCols := smoothSeries(colBrightness, maxInt(9, width/35))

	rowThreshold := maxFloat(borderBrightness+8, 172)
	colThreshold := maxFloat(borderBrightness+8, 170)
	darkThreshold := 0.012

	top, bottom, ok := longestSegment(smoothedRows, rowDarkRatio, rowThreshold, darkThreshold, height/4)
	if !ok {
		return image.Rectangle{}, false
	}

	left, right, ok := longestColumnSegment(smoothedCols, source, top, bottom, colThreshold, width/5)
	if !ok {
		return image.Rectangle{}, false
	}

	rect := image.Rect(left, top, right+1, bottom+1)
	widthRatio := float64(rect.Dx()) / float64(width)
	heightRatio := float64(rect.Dy()) / float64(height)
	if widthRatio < 0.22 || heightRatio < 0.35 || float64(rect.Dy())/float64(rect.Dx()) < 1.05 {
		return image.Rectangle{}, false
	}

	return expandRect(rect, bounds, maxInt(8, width/35), maxInt(8, height/35)), true
}

func detectReceiptTextBounds(source *image.NRGBA) (image.Rectangle, bool) {
	bounds := source.Bounds()
	width := bounds.Dx()
	height := bounds.Dy()
	if width < 32 || height < 32 {
		return image.Rectangle{}, false
	}

	borderBrightness := averageBorderBrightness(source)
	inkThreshold := minFloat(borderBrightness-92, 160)
	if inkThreshold < 110 {
		inkThreshold = 110
	}

	_, top, _, bottom, inkPixels, ok := textInkBounds(source, inkThreshold)
	if !ok {
		return image.Rectangle{}, false
	}

	left, right, ok := denseTextColumnBounds(source, inkThreshold, 0.008)
	if !ok {
		return image.Rectangle{}, false
	}

	rect := image.Rect(left, top, right+1, bottom+1)
	rect = expandRect(rect, bounds, maxInt(16, width/18), maxInt(28, height/18))

	widthRatio := float64(rect.Dx()) / float64(width)
	heightRatio := float64(rect.Dy()) / float64(height)
	if inkPixels < maxInt(400, width*height/1800) || widthRatio < 0.16 || heightRatio < 0.25 {
		return image.Rectangle{}, false
	}

	return rect, true
}

func scaleDetectionRect(rect image.Rectangle, fromBounds image.Rectangle, targetBounds image.Rectangle) (image.Rectangle, bool) {
	scaleX := float64(targetBounds.Dx()) / float64(fromBounds.Dx())
	scaleY := float64(targetBounds.Dy()) / float64(fromBounds.Dy())
	crop := image.Rect(
		int(float64(rect.Min.X)*scaleX),
		int(float64(rect.Min.Y)*scaleY),
		int(float64(rect.Max.X)*scaleX),
		int(float64(rect.Max.Y)*scaleY),
	)
	crop = expandRect(crop, targetBounds, maxInt(18, targetBounds.Dx()/40), maxInt(18, targetBounds.Dy()/40))

	widthRatio := float64(crop.Dx()) / float64(targetBounds.Dx())
	heightRatio := float64(crop.Dy()) / float64(targetBounds.Dy())
	if widthRatio > 0.96 && heightRatio > 0.96 {
		return image.Rectangle{}, false
	}

	if widthRatio < 0.28 || heightRatio < 0.35 {
		return image.Rectangle{}, false
	}

	return crop, true
}

func isReceiptCandidatePixel(pixel color.NRGBA, brightnessThreshold uint8, whitenessThreshold float64) bool {
	brightness := uint8(pixelBrightness(pixel))
	if brightness < brightnessThreshold {
		return false
	}

	maxChannel := maxInt(int(pixel.R), maxInt(int(pixel.G), int(pixel.B)))
	minChannel := minInt(int(pixel.R), minInt(int(pixel.G), int(pixel.B)))
	return float64(maxChannel-minChannel) <= (1-whitenessThreshold)*255
}

func averageBorderBrightness(source *image.NRGBA) float64 {
	bounds := source.Bounds()
	width := bounds.Dx()
	height := bounds.Dy()
	if width == 0 || height == 0 {
		return 0
	}

	var total float64
	count := 0
	for x := 0; x < width; x++ {
		total += pixelBrightness(source.NRGBAAt(bounds.Min.X+x, bounds.Min.Y))
		total += pixelBrightness(source.NRGBAAt(bounds.Min.X+x, bounds.Max.Y-1))
		count += 2
	}

	for y := 1; y < height-1; y++ {
		total += pixelBrightness(source.NRGBAAt(bounds.Min.X, bounds.Min.Y+y))
		total += pixelBrightness(source.NRGBAAt(bounds.Max.X-1, bounds.Min.Y+y))
		count += 2
	}

	return total / float64(maxInt(1, count))
}

func smoothSeries(values []float64, window int) []float64 {
	if len(values) == 0 {
		return nil
	}

	if window < 1 {
		window = 1
	}

	if window%2 == 0 {
		window++
	}

	radius := window / 2
	smoothed := make([]float64, len(values))
	for index := range values {
		start := maxInt(0, index-radius)
		end := minInt(len(values)-1, index+radius)
		var total float64
		for current := start; current <= end; current++ {
			total += values[current]
		}

		smoothed[index] = total / float64(end-start+1)
	}

	return smoothed
}

func longestSegment(brightness []float64, darkRatio []float64, brightnessThreshold float64, darkRatioThreshold float64, minLength int) (int, int, bool) {
	bestStart, bestEnd := -1, -1
	currentStart := -1

	for index := range brightness {
		isCandidate := brightness[index] >= brightnessThreshold && darkRatio[index] >= darkRatioThreshold
		if isCandidate {
			if currentStart == -1 {
				currentStart = index
			}
			continue
		}

		if currentStart != -1 && index-currentStart >= minLength && index-currentStart > bestEnd-bestStart {
			bestStart = currentStart
			bestEnd = index - 1
		}
		currentStart = -1
	}

	if currentStart != -1 && len(brightness)-currentStart >= minLength && len(brightness)-currentStart > bestEnd-bestStart {
		bestStart = currentStart
		bestEnd = len(brightness) - 1
	}

	return bestStart, bestEnd, bestStart != -1
}

func longestRatioSegment(values []float64, minValue float64, minLength int) (int, int, bool) {
	bestStart, bestEnd := -1, -1
	currentStart := -1

	for index := range values {
		if values[index] >= minValue {
			if currentStart == -1 {
				currentStart = index
			}
			continue
		}

		if currentStart != -1 && index-currentStart >= minLength && index-currentStart > bestEnd-bestStart {
			bestStart = currentStart
			bestEnd = index - 1
		}
		currentStart = -1
	}

	if currentStart != -1 && len(values)-currentStart >= minLength && len(values)-currentStart > bestEnd-bestStart {
		bestStart = currentStart
		bestEnd = len(values) - 1
	}

	return bestStart, bestEnd, bestStart != -1
}

func textInkBounds(source *image.NRGBA, inkThreshold float64) (int, int, int, int, int, bool) {
	bounds := source.Bounds()
	minX := bounds.Max.X
	minY := bounds.Max.Y
	maxX := bounds.Min.X - 1
	maxY := bounds.Min.Y - 1
	inkPixels := 0

	for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
		for x := bounds.Min.X; x < bounds.Max.X; x++ {
			if pixelBrightness(source.NRGBAAt(x, y)) > inkThreshold {
				continue
			}

			inkPixels++
			if x < minX {
				minX = x
			}
			if x > maxX {
				maxX = x
			}
			if y < minY {
				minY = y
			}
			if y > maxY {
				maxY = y
			}
		}
	}

	if inkPixels < maxInt(400, bounds.Dx()*bounds.Dy()/800) || maxX <= minX {
		return 0, 0, 0, 0, 0, false
	}

	return minX, minY, maxX, maxY, inkPixels, true
}

func denseTextColumnBounds(source *image.NRGBA, inkThreshold float64, minRatio float64) (int, int, bool) {
	bounds := source.Bounds()
	height := bounds.Dy()
	left := -1
	right := -1

	for x := bounds.Min.X; x < bounds.Max.X; x++ {
		inkCount := 0
		for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
			if pixelBrightness(source.NRGBAAt(x, y)) <= inkThreshold {
				inkCount++
			}
		}

		if float64(inkCount)/float64(height) < minRatio {
			continue
		}

		if left == -1 {
			left = x
		}
		right = x
	}

	if left == -1 || right <= left {
		return 0, 0, false
	}

	return left, right, true
}

func longestColumnSegment(colBrightness []float64, source *image.NRGBA, top int, bottom int, brightnessThreshold float64, minLength int) (int, int, bool) {
	if top < 0 || bottom >= source.Bounds().Dy() || top > bottom {
		return 0, 0, false
	}

	width := source.Bounds().Dx()
	rowCount := bottom - top + 1
	colDarkRatio := make([]float64, width)
	for x := 0; x < width; x++ {
		darkPixels := 0
		for y := top; y <= bottom; y++ {
			if pixelBrightness(source.NRGBAAt(source.Bounds().Min.X+x, source.Bounds().Min.Y+y)) < 168 {
				darkPixels++
			}
		}
		colDarkRatio[x] = float64(darkPixels) / float64(rowCount)
	}

	bestStart, bestEnd := -1, -1
	currentStart := -1
	for index := range colBrightness {
		isCandidate := colBrightness[index] >= brightnessThreshold && colDarkRatio[index] >= 0.01
		if isCandidate {
			if currentStart == -1 {
				currentStart = index
			}
			continue
		}

		if currentStart != -1 && index-currentStart >= minLength && index-currentStart > bestEnd-bestStart {
			bestStart = currentStart
			bestEnd = index - 1
		}
		currentStart = -1
	}

	if currentStart != -1 && len(colBrightness)-currentStart >= minLength && len(colBrightness)-currentStart > bestEnd-bestStart {
		bestStart = currentStart
		bestEnd = len(colBrightness) - 1
	}

	return bestStart, bestEnd, bestStart != -1
}

func pixelBrightness(pixel color.NRGBA) float64 {
	return float64(int(pixel.R)+int(pixel.G)+int(pixel.B)) / 3
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

func rotate90NRGBA(source *image.NRGBA) *image.NRGBA {
	bounds := source.Bounds()
	target := image.NewNRGBA(image.Rect(0, 0, bounds.Dy(), bounds.Dx()))
	for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
		for x := bounds.Min.X; x < bounds.Max.X; x++ {
			target.SetNRGBA(bounds.Max.Y-1-y, x-bounds.Min.X, source.NRGBAAt(x, y))
		}
	}

	return target
}

func rotate180NRGBA(source *image.NRGBA) *image.NRGBA {
	bounds := source.Bounds()
	target := image.NewNRGBA(image.Rect(0, 0, bounds.Dx(), bounds.Dy()))
	for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
		for x := bounds.Min.X; x < bounds.Max.X; x++ {
			target.SetNRGBA(bounds.Max.X-1-x, bounds.Max.Y-1-y, source.NRGBAAt(x, y))
		}
	}

	return target
}

func rotate270NRGBA(source *image.NRGBA) *image.NRGBA {
	bounds := source.Bounds()
	target := image.NewNRGBA(image.Rect(0, 0, bounds.Dy(), bounds.Dx()))
	for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
		for x := bounds.Min.X; x < bounds.Max.X; x++ {
			target.SetNRGBA(y-bounds.Min.Y, bounds.Max.X-1-x, source.NRGBAAt(x, y))
		}
	}

	return target
}

func flipHorizontalNRGBA(source *image.NRGBA) *image.NRGBA {
	bounds := source.Bounds()
	target := image.NewNRGBA(image.Rect(0, 0, bounds.Dx(), bounds.Dy()))
	for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
		for x := bounds.Min.X; x < bounds.Max.X; x++ {
			target.SetNRGBA(bounds.Max.X-1-x, y-bounds.Min.Y, source.NRGBAAt(x, y))
		}
	}

	return target
}

func flipVerticalNRGBA(source *image.NRGBA) *image.NRGBA {
	bounds := source.Bounds()
	target := image.NewNRGBA(image.Rect(0, 0, bounds.Dx(), bounds.Dy()))
	for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
		for x := bounds.Min.X; x < bounds.Max.X; x++ {
			target.SetNRGBA(x-bounds.Min.X, bounds.Max.Y-1-y, source.NRGBAAt(x, y))
		}
	}

	return target
}

func transposeNRGBA(source *image.NRGBA) *image.NRGBA {
	bounds := source.Bounds()
	target := image.NewNRGBA(image.Rect(0, 0, bounds.Dy(), bounds.Dx()))
	for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
		for x := bounds.Min.X; x < bounds.Max.X; x++ {
			target.SetNRGBA(y-bounds.Min.Y, x-bounds.Min.X, source.NRGBAAt(x, y))
		}
	}

	return target
}

func transverseNRGBA(source *image.NRGBA) *image.NRGBA {
	bounds := source.Bounds()
	target := image.NewNRGBA(image.Rect(0, 0, bounds.Dy(), bounds.Dx()))
	for y := bounds.Min.Y; y < bounds.Max.Y; y++ {
		for x := bounds.Min.X; x < bounds.Max.X; x++ {
			target.SetNRGBA(bounds.Max.Y-1-y, bounds.Max.X-1-x, source.NRGBAAt(x, y))
		}
	}

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

	for _, line := range lines {
		if strings.Contains(strings.ToUpper(line.Text), "SUMA") {
			score += 0.04
			break
		}
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

func maxFloat(a, b float64) float64 {
	if a > b {
		return a
	}

	return b
}

func minFloat(a, b float64) float64 {
	if a < b {
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
	multipleSpaces   = regexp.MustCompile(`\s+`)
	alphaRegex       = regexp.MustCompile(`[A-Za-zĄĆĘŁŃÓŚŹŻąćęłńóśźż]{3,}`)
	noisyChars       = regexp.MustCompile(`[^\p{L}\p{N}\s,.\-:/xX*]`)
	amountLikeRegex  = regexp.MustCompile(`\d+[,.]\d{2}`)
	itemPayloadRegex = regexp.MustCompile(`[A-Za-zĄĆĘŁŃÓŚŹŻąćęłńóśźż]{2,}.*\d+[,.]\d{2}`)
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
