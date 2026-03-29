import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { ReceiptApiService } from './receipt-api.service';
import {
  BoundingBox,
  CreateReceiptResponse,
  JobResponse,
  OcrLine,
  ReceiptItem,
  ReceiptItemUpdateRequest,
  ReceiptListItem,
  ReceiptResponse,
  UpdateReceiptRequest
} from './receipt.models';

type Screen = 'list' | 'detail' | 'review';
type SummaryFieldKey = 'merchantName' | 'taxId' | 'purchaseDate' | 'totalGross';

interface ReviewDraft {
  summary: {
    merchantName: string;
    taxId: string;
    purchaseDate: string;
    currency: string;
    totalGross: string;
  };
  items: ReviewItemDraft[];
}

interface ReviewItemDraft {
  name: string;
  quantity: string;
  unitPrice: string;
  totalPrice: string;
  discount: string;
  vatRate: string;
  sourceLine: string;
  sourceLines: string[];
  sourceLineNumbers: number[];
  wasAiCorrected: boolean;
  repairReason: string;
  candidateKind: ReceiptItem['candidateKind'];
}

interface ReviewSuggestion {
  key: SummaryFieldKey;
  label: string;
  currentValue: string;
  suggestedValue: string;
  status: string;
  needsAttention: boolean;
}

interface ReferenceSelection {
  title: string;
  subtitle: string;
  lineNumbers: number[];
  sourceLines: string[];
}

@Component({
  selector: 'app-root',
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  private readonly api = inject(ReceiptApiService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly screen = signal<Screen>('list');
  protected readonly archive = signal<ReceiptListItem[]>([]);
  protected readonly selectedReceipt = signal<ReceiptResponse | null>(null);
  protected readonly selectedJob = signal<JobResponse | null>(null);
  protected readonly reviewDraft = signal<ReviewDraft | null>(null);
  protected readonly activeReference = signal<ReferenceSelection | null>(null);
  protected readonly isUploading = signal(false);
  protected readonly isLoadingArchive = signal(false);
  protected readonly isSavingReview = signal(false);
  protected readonly deletingReceiptId = signal<string | null>(null);
  protected readonly statusMessage = signal('Ready to scan a receipt from your phone.');
  protected selectedFile: File | null = null;

  protected readonly phoneTitle = computed(() => {
    switch (this.screen()) {
      case 'detail':
        return 'Receipt Detail';
      case 'review':
        return 'Verify & Update';
      default:
        return 'Receipts';
    }
  });

  protected readonly referenceLines = computed(() => {
    const receipt = this.selectedReceipt();
    const reference = this.activeReference();

    if (!receipt || !reference) {
      return [] as OcrLine[];
    }

    return receipt.ocrLines.filter((line) => reference.lineNumbers.includes(line.lineNumber));
  });

  protected readonly referenceBoxes = computed(() => {
    return this.referenceLines()
      .filter((line) => !!line.boundingBox)
      .map((line) => ({ lineNumber: line.lineNumber, box: line.boundingBox as BoundingBox }));
  });

  protected readonly overlaySize = computed(() => {
    const receipt = this.selectedReceipt();
    if (!receipt) {
      return { width: 1000, height: 1400 };
    }

    const widthFromMetadata = receipt.imageMetadata?.width ?? 0;
    const heightFromMetadata = receipt.imageMetadata?.height ?? 0;
    if (widthFromMetadata > 0 && heightFromMetadata > 0) {
      return { width: widthFromMetadata, height: heightFromMetadata };
    }

    const boxes = receipt.ocrLines
      .filter((line) => !!line.boundingBox)
      .map((line) => line.boundingBox as BoundingBox);

    if (boxes.length === 0) {
      return { width: 1000, height: 1400 };
    }

    return {
      width: Math.max(...boxes.map((box) => box.x + box.width), 1000),
      height: Math.max(...boxes.map((box) => box.y + box.height), 1400)
    };
  });

  constructor() {
    this.loadArchive();
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile = input.files?.item(0) ?? null;
    this.statusMessage.set(this.selectedFile
      ? `Selected ${this.selectedFile.name}.`
      : 'Choose a receipt image to begin.');
  }

  protected uploadReceipt(): void {
    if (!this.selectedFile) {
      this.statusMessage.set('Choose a receipt image before uploading.');
      return;
    }

    this.isUploading.set(true);
    this.statusMessage.set('Uploading receipt and creating processing job...');

    this.api.uploadReceipt(this.selectedFile)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.isUploading.set(false))
      )
      .subscribe({
        next: (response) => {
          this.statusMessage.set(`Receipt accepted. Job ${response.jobId} is running.`);
          this.screen.set('detail');
          this.trackJob(response);
        },
        error: (error: HttpErrorResponse) => {
          this.statusMessage.set(error.message || 'Upload failed.');
        }
      });
  }

  protected openReceipt(receiptId: string, targetScreen: Screen = 'detail'): void {
    this.api.getReceipt(receiptId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (receipt) => {
          this.selectedReceipt.set(receipt);
          this.reviewDraft.set(null);
          this.activeReference.set(null);
          this.screen.set(targetScreen);
          this.statusMessage.set(`Loaded receipt ${receipt.id}.`);
        },
        error: () => this.statusMessage.set('Could not load receipt details.')
      });
  }

  protected refreshArchive(): void {
    this.loadArchive();
  }

  protected backToList(): void {
    this.reviewDraft.set(null);
    this.activeReference.set(null);
    this.screen.set('list');
  }

  protected enterReview(): void {
    const receipt = this.selectedReceipt();
    if (!receipt) {
      return;
    }

    this.reviewDraft.set(this.buildDraft(receipt));
    this.activeReference.set(null);
    this.screen.set('review');
  }

  protected cancelReview(): void {
    this.reviewDraft.set(null);
    this.activeReference.set(null);
    this.screen.set('detail');
  }

  protected addDraftItem(): void {
    const draft = this.reviewDraft();
    if (!draft) {
      return;
    }

    draft.items.push({
      name: '',
      quantity: '',
      unitPrice: '',
      totalPrice: '',
      discount: '',
      vatRate: '',
      sourceLine: '',
      sourceLines: [],
      sourceLineNumbers: [],
      wasAiCorrected: false,
      repairReason: '',
      candidateKind: 'Standard'
    });

    this.reviewDraft.set({
      summary: { ...draft.summary },
      items: [...draft.items]
    });
  }

  protected removeDraftItem(index: number): void {
    const draft = this.reviewDraft();
    if (!draft) {
      return;
    }

    draft.items.splice(index, 1);
    this.reviewDraft.set({
      summary: { ...draft.summary },
      items: [...draft.items]
    });
  }

  protected saveReview(): void {
    const receipt = this.selectedReceipt();
    const draft = this.reviewDraft();
    if (!receipt || !draft) {
      return;
    }

    const request = this.buildUpdateRequest(draft);
    this.isSavingReview.set(true);
    this.statusMessage.set('Saving manual updates...');

    this.api.updateReceipt(receipt.id, request)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.isSavingReview.set(false))
      )
      .subscribe({
        next: (updatedReceipt) => {
          this.selectedReceipt.set(updatedReceipt);
          this.reviewDraft.set(this.buildDraft(updatedReceipt));
          this.screen.set('detail');
          this.activeReference.set(null);
          this.statusMessage.set('Receipt updated successfully.');
          this.loadArchive(updatedReceipt.id);
        },
        error: (error: HttpErrorResponse) => {
          this.statusMessage.set(error.message || 'Could not save receipt updates.');
        }
      });
  }

  protected deleteReceipt(receiptId: string, event?: Event): void {
    event?.stopPropagation();

    const confirmed = window.confirm('Delete this receipt from the archive?');
    if (!confirmed) {
      return;
    }

    this.deletingReceiptId.set(receiptId);
    this.api.deleteReceipt(receiptId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.deletingReceiptId.set(null))
      )
      .subscribe({
        next: () => {
          const selectedId = this.selectedReceipt()?.id;
          if (selectedId === receiptId) {
            this.selectedReceipt.set(null);
            this.reviewDraft.set(null);
            this.activeReference.set(null);
            this.screen.set('list');
          }

          this.statusMessage.set('Receipt deleted.');
          this.loadArchive();
        },
        error: () => this.statusMessage.set('Could not delete receipt.')
      });
  }

  protected formatStatus(status: string): string {
    return status === 'CompletedWithWarnings' ? 'Needs review' : status;
  }

  protected formatConfidence(confidence: number | null | undefined): string {
    return `${Math.round((confidence ?? 0) * 100)}%`;
  }

  protected merchantName(receipt: ReceiptResponse): string {
    return receipt.receiptSummary.merchantName || 'Unknown merchant';
  }

  protected getReviewSuggestions(): ReviewSuggestion[] {
    const receipt = this.selectedReceipt();
    const draft = this.reviewDraft();
    if (!receipt || !draft) {
      return [];
    }

    return [
      {
        key: 'merchantName',
        label: 'Merchant',
        currentValue: draft.summary.merchantName || 'Empty',
        suggestedValue: receipt.extractedReceiptSummary.merchantName || 'No OCR suggestion',
        status: receipt.receiptSummary.confidence < 0.78 ? 'Low confidence' : 'Looks stable',
        needsAttention: receipt.receiptSummary.confidence < 0.78
      },
      {
        key: 'taxId',
        label: 'NIP',
        currentValue: draft.summary.taxId || 'Empty',
        suggestedValue: receipt.extractedReceiptSummary.taxId || 'No OCR suggestion',
        status: receipt.receiptSummary.taxId ? 'Detected' : 'Missing',
        needsAttention: !receipt.receiptSummary.taxId
      },
      {
        key: 'purchaseDate',
        label: 'Date',
        currentValue: draft.summary.purchaseDate || 'Empty',
        suggestedValue: receipt.extractedReceiptSummary.purchaseDate || 'No OCR suggestion',
        status: receipt.receiptSummary.purchaseDate ? 'Detected' : 'Missing',
        needsAttention: !receipt.receiptSummary.purchaseDate
      },
      {
        key: 'totalGross',
        label: 'Total',
        currentValue: draft.summary.totalGross || 'Empty',
        suggestedValue: this.formatNullableAmount(receipt.extractedReceiptSummary.totalGross),
        status: receipt.consistency.consistencyStatus,
        needsAttention: receipt.consistency.needsReview
      }
    ];
  }

  protected getProblemItems(receipt: ReceiptResponse): ReceiptItem[] {
    return receipt.items.filter((item) =>
      item.parseWarnings.length > 0
      || item.wasAiCorrected
      || item.confidence < 0.72
      || item.excludedByBalancer);
  }

  protected focusSuggestion(key: SummaryFieldKey): void {
    const receipt = this.selectedReceipt();
    if (!receipt) {
      return;
    }

    const lineNumbers = this.resolveSummaryLineNumbers(receipt, key);
    const sourceLines = receipt.ocrLines
      .filter((line) => lineNumbers.includes(line.lineNumber))
      .map((line) => line.rawText);

    this.activeReference.set({
      title: this.getSummaryLabel(key),
      subtitle: `Matched ${lineNumbers.length} OCR line(s)`,
      lineNumbers,
      sourceLines
    });
  }

  protected focusItem(item: ReviewItemDraft | ReceiptItem): void {
    const lineNumbers = 'sourceLineNumbers' in item ? item.sourceLineNumbers : [];
    const sourceLines = item.sourceLines.length > 0
      ? item.sourceLines
      : (item.sourceLine ? [item.sourceLine] : []);

    this.activeReference.set({
      title: item.name || 'Manual item',
      subtitle: lineNumbers.length > 0 ? `Matched ${lineNumbers.length} OCR line(s)` : 'No OCR box available',
      lineNumbers,
      sourceLines
    });
  }

  protected highlightOcrLine(line: OcrLine): void {
    this.activeReference.set({
      title: 'OCR line',
      subtitle: line.lineType,
      lineNumbers: [line.lineNumber],
      sourceLines: [line.rawText]
    });
  }

  protected isReceiptSelected(receiptId: string): boolean {
    return this.selectedReceipt()?.id === receiptId;
  }

  protected deleting(receiptId: string): boolean {
    return this.deletingReceiptId() === receiptId;
  }

  protected trackArchive(_: number, receipt: ReceiptListItem): string {
    return receipt.id;
  }

  protected trackOcrLine(_: number, line: OcrLine): string {
    return `${line.lineNumber}-${line.rawText}`;
  }

  protected trackItem(index: number, item: ReviewItemDraft | ReceiptItem): string {
    return `${index}-${item.name}-${item.sourceLine}`;
  }

  private loadArchive(preferredReceiptId?: string): void {
    this.isLoadingArchive.set(true);
    this.api.listReceipts()
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.isLoadingArchive.set(false))
      )
      .subscribe({
        next: (receipts) => {
          this.archive.set(receipts);

          if (receipts.length === 0) {
            this.selectedReceipt.set(null);
            this.screen.set('list');
            return;
          }

          const selectedId = preferredReceiptId ?? this.selectedReceipt()?.id;
          if (!selectedId) {
            return;
          }

          if (!receipts.some((item) => item.id === selectedId)) {
            return;
          }

          this.openReceipt(selectedId, this.screen() === 'review' ? 'review' : 'detail');
        },
        error: () => this.statusMessage.set('API archive is not reachable yet.')
      });
  }

  private trackJob(response: CreateReceiptResponse): void {
    this.api.getReceipt(response.receiptId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (receipt) => this.selectedReceipt.set(receipt)
      });

    this.api.pollJob(response.jobId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (job) => {
          this.selectedJob.set(job);
          if (job.stage === 'Completed' || job.stage === 'Failed') {
            this.statusMessage.set(`Job finished with stage ${job.stage}.`);
            this.openReceipt(response.receiptId);
            this.loadArchive(response.receiptId);
          }
        },
        error: () => this.statusMessage.set('Could not refresh job status.')
      });
  }

  private buildDraft(receipt: ReceiptResponse): ReviewDraft {
    return {
      summary: {
        merchantName: receipt.receiptSummary.merchantName ?? '',
        taxId: receipt.receiptSummary.taxId ?? '',
        purchaseDate: receipt.receiptSummary.purchaseDate ?? '',
        currency: receipt.receiptSummary.currency || 'PLN',
        totalGross: this.toInputValue(receipt.receiptSummary.totalGross)
      },
      items: receipt.items.map((item) => ({
        name: item.name,
        quantity: this.toInputValue(item.quantity),
        unitPrice: this.toInputValue(item.unitPrice),
        totalPrice: this.toInputValue(item.totalPrice),
        discount: this.toInputValue(item.discount),
        vatRate: item.vatRate ?? '',
        sourceLine: item.sourceLine,
        sourceLines: [...item.sourceLines],
        sourceLineNumbers: [...item.sourceLineNumbers],
        wasAiCorrected: item.wasAiCorrected,
        repairReason: item.repairReason ?? '',
        candidateKind: item.candidateKind
      }))
    };
  }

  private buildUpdateRequest(draft: ReviewDraft): UpdateReceiptRequest {
    return {
      receiptSummary: {
        merchantName: this.nullIfEmpty(draft.summary.merchantName),
        taxId: this.nullIfEmpty(draft.summary.taxId),
        purchaseDate: this.nullIfEmpty(draft.summary.purchaseDate),
        currency: draft.summary.currency || 'PLN',
        totalGross: this.toNullableNumber(draft.summary.totalGross)
      },
      items: draft.items
        .filter((item) => item.name.trim().length > 0)
        .map((item): ReceiptItemUpdateRequest => ({
          name: item.name.trim(),
          quantity: this.toNullableNumber(item.quantity),
          unitPrice: this.toNullableNumber(item.unitPrice),
          totalPrice: this.toNullableNumber(item.totalPrice),
          discount: this.toNullableNumber(item.discount),
          vatRate: this.nullIfEmpty(item.vatRate),
          sourceLine: item.sourceLine,
          sourceLines: item.sourceLines,
          sourceLineNumbers: item.sourceLineNumbers,
          wasAiCorrected: item.wasAiCorrected,
          repairReason: this.nullIfEmpty(item.repairReason),
          candidateKind: item.candidateKind
        }))
    };
  }

  private resolveSummaryLineNumbers(receipt: ReceiptResponse, key: SummaryFieldKey): number[] {
    const matchByContent = (value: string | null | undefined): number[] => {
      const normalizedValue = value?.trim().toLowerCase();
      if (!normalizedValue) {
        return [];
      }

      return receipt.ocrLines
        .filter((line) => line.normalizedText.toLowerCase().includes(normalizedValue))
        .map((line) => line.lineNumber);
    };

    switch (key) {
      case 'merchantName':
        return matchByContent(receipt.extractedReceiptSummary.merchantName);
      case 'taxId':
        return matchByContent(receipt.extractedReceiptSummary.taxId);
      case 'purchaseDate':
        return matchByContent(receipt.extractedReceiptSummary.purchaseDate);
      case 'totalGross': {
        const totalMatches = receipt.ocrLines
          .filter((line) =>
            line.lineType === 'Total'
            || line.normalizedText.toLowerCase().includes(this.formatNullableAmount(receipt.extractedReceiptSummary.totalGross).replace(' PLN', '').toLowerCase()))
          .map((line) => line.lineNumber);
        return [...new Set(totalMatches)];
      }
    }
  }

  private getSummaryLabel(key: SummaryFieldKey): string {
    switch (key) {
      case 'merchantName':
        return 'Merchant';
      case 'taxId':
        return 'NIP';
      case 'purchaseDate':
        return 'Date';
      case 'totalGross':
        return 'Total';
    }
  }

  private toInputValue(value: number | string | null | undefined): string {
    if (value === null || value === undefined || value === '') {
      return '';
    }

    return `${value}`;
  }

  private toNullableNumber(value: string): number | null {
    const normalized = value.replace(',', '.').trim();
    if (!normalized) {
      return null;
    }

    const parsed = Number(normalized);
    return Number.isFinite(parsed) ? parsed : null;
  }

  private nullIfEmpty(value: string): string | null {
    const normalized = value.trim();
    return normalized.length > 0 ? normalized : null;
  }

  private formatNullableAmount(value: number | null | undefined): string {
    return value === null || value === undefined ? 'No OCR suggestion' : `${value.toFixed(2)} PLN`;
  }
}
