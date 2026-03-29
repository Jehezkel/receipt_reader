export interface ReceiptSummary {
  merchantName?: string | null;
  taxId?: string | null;
  purchaseDate?: string | null;
  currency: string;
  totalGross?: number | null;
  confidence: number;
  totalMatchesItems: boolean;
  needsReview: boolean;
}

export interface ReceiptConsistencyResult {
  declaredTotal?: number | null;
  calculatedItemsTotal?: number | null;
  calculatedItemsTotalAfterDiscounts?: number | null;
  differenceToDeclaredTotal?: number | null;
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
  sourceLine: string;
  sourceLines: string[];
  wasAiCorrected: boolean;
  parseWarnings: string[];
}

export interface OcrLine {
  rawText: string;
  normalizedText: string;
  text: string;
  confidence: number;
  lineType: 'Unknown' | 'Header' | 'ItemCandidate' | 'Subtotal' | 'Total' | 'Vat' | 'Discount' | 'Payment' | 'Technical';
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
  receiptSummary: ReceiptSummary;
  consistency: ReceiptConsistencyResult;
  items: ReceiptItem[];
  confidence: number;
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
