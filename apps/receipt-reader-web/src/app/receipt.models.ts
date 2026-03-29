export interface ReceiptSummary {
  merchantName?: string | null;
  taxId?: string | null;
  purchaseDate?: string | null;
  currency: string;
  totalGross?: number | null;
  paymentsTotal?: number | null;
  vatBreakdownTotal?: number | null;
  declaredSubtotal?: number | null;
  totalSourceLine?: string | null;
  confidence: number;
  totalMatchesItems: boolean;
  needsReview: boolean;
}

export interface ReceiptConsistencyResult {
  declaredTotal?: number | null;
  calculatedItemsTotal?: number | null;
  calculatedItemsTotalAfterDiscounts?: number | null;
  paymentsTotal?: number | null;
  vatBreakdownTotal?: number | null;
  differenceToDeclaredTotal?: number | null;
  differenceToPaymentsTotal?: number | null;
  differenceToVatBreakdownTotal?: number | null;
  consistencyStatus: 'Exact' | 'ToleranceMatch' | 'Mismatch' | 'InsufficientData';
  needsReview: boolean;
}

export interface ReceiptItem {
  name: string;
  quantity?: number | null;
  unitPrice?: number | null;
  totalPrice?: number | null;
  discount?: number | null;
  vatRate?: string | null;
  confidence: number;
  arithmeticConfidence: number;
  candidateKind: 'Standard' | 'Weighted' | 'MultiLine' | 'DiscountAdjusted' | 'Repaired' | 'Excluded';
  section: string;
  vatCode?: string | null;
  sourceLine: string;
  sourceLines: string[];
  evidenceLines: string[];
  recognitionHints: string[];
  wasReconstructedFromMultipleLines: boolean;
  wasAiCorrected: boolean;
  excludedByBalancer: boolean;
  repairReason?: string | null;
  parseWarnings: string[];
}

export interface ReceiptPayment {
  method: string;
  amount?: number | null;
  sourceLine: string;
}

export interface OcrLine {
  lineNumber: number;
  rawText: string;
  normalizedText: string;
  text: string;
  confidence: number;
  characterCount: number;
  lineType: 'Unknown' | 'Header' | 'ItemCandidate' | 'Subtotal' | 'Total' | 'Vat' | 'Discount' | 'Payment' | 'Technical';
  section: string;
  variantId: string;
  alternateTexts: string[];
}

export interface OcrVariantArtifact {
  variantId: string;
  variantType: string;
  section: string;
  psm: number;
  cropBox?: { x: number; y: number; width: number; height: number } | null;
  rotationDegrees: number;
  appliedFilters: string[];
  estimatedReadabilityScore: number;
  qualityScore: number;
  selected: boolean;
  rawText: string;
  normalizedText: string;
}

export interface SectionConfidenceArtifact {
  section: string;
  confidence: number;
  selectedVariantId?: string | null;
  notes?: string | null;
}

export interface ProcessingStep {
  stage: string;
  status: string;
  details: string;
  timestamp: string;
}

export interface ReceiptListItem {
  id: string;
  status: string;
  imageUrl: string;
  merchantName?: string | null;
  purchaseDate?: string | null;
  totalGross?: number | null;
  needsReview: boolean;
  consistencyStatus: 'Exact' | 'ToleranceMatch' | 'Mismatch' | 'InsufficientData';
  confidence: number;
  createdAt: string;
}

export interface ReceiptResponse {
  id: string;
  status: string;
  imageUrl: string;
  createdAt: string;
  rawOcrText: string;
  normalizedLines: string[];
  ocrLines: OcrLine[];
  ocrVariants: OcrVariantArtifact[];
  selectedOcrVariant?: string | null;
  sectionConfidences: SectionConfidenceArtifact[];
  receiptSummary: ReceiptSummary;
  consistency: ReceiptConsistencyResult;
  items: ReceiptItem[];
  payments: ReceiptPayment[];
  confidence: number;
  aiWasTriggeredBecause?: string | null;
  totalEvidence?: string | null;
  processingSteps: ProcessingStep[];
}

export interface JobResponse {
  id: string;
  receiptId: string;
  stage: string;
  startedAt: string;
  finishedAt?: string | null;
  errorCode?: string | null;
  provider: string;
}

export interface CreateReceiptResponse {
  receiptId: string;
  jobId: string;
  statusUrl: string;
  receiptUrl: string;
}
