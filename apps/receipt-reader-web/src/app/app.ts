import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { ReceiptApiService } from './receipt-api.service';
import { CreateReceiptResponse, JobResponse, ReceiptListItem, ReceiptResponse } from './receipt.models';

@Component({
  selector: 'app-root',
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  private readonly api = inject(ReceiptApiService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly archive = signal<ReceiptListItem[]>([]);
  protected readonly selectedReceipt = signal<ReceiptResponse | null>(null);
  protected readonly selectedJob = signal<JobResponse | null>(null);
  protected readonly isUploading = signal(false);
  protected readonly isLoadingArchive = signal(false);
  protected readonly statusMessage = signal('Ready to scan a receipt from your phone.');
  protected selectedFile: File | null = null;

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
          this.trackJob(response);
        },
        error: (error: HttpErrorResponse) => {
          this.statusMessage.set(error.message || 'Upload failed.');
        }
      });
  }

  protected openReceipt(receiptId: string): void {
    this.api.getReceipt(receiptId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (receipt) => {
          this.selectedReceipt.set(receipt);
          this.statusMessage.set(`Loaded receipt ${receipt.id}.`);
        },
        error: () => this.statusMessage.set('Could not load receipt details.')
      });
  }

  protected refreshArchive(): void {
    this.loadArchive();
  }

  protected formatStatus(status: string): string {
    return status === 'CompletedWithWarnings' ? 'Needs review' : status;
  }

  private loadArchive(): void {
    this.isLoadingArchive.set(true);
    this.api.listReceipts()
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.isLoadingArchive.set(false))
      )
      .subscribe({
        next: (receipts) => {
          this.archive.set(receipts);
          if (receipts.length > 0 && !this.selectedReceipt()) {
            this.openReceipt(receipts[0].id);
          }
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
            this.loadArchive();
          }
        },
        error: () => this.statusMessage.set('Could not refresh job status.')
      });
  }
}
