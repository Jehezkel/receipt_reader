export interface ReceiptSummary {
  merchantName?: string | null;
  taxId?: string | null;
  purchaseDate?: string | null;
  currency: string;
  totalGross?: number | null;
  confidence: number;
  totalMatchesItems: boolean;
}

export interface ReceiptItem {
  name: string;
  quantity?: number | null;
  unitPrice?: number | null;
  totalPrice?: number | null;
  vatRate?: string | null;
  confidence: number;
  sourceLine: string;
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
  receiptSummary: ReceiptSummary;
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
