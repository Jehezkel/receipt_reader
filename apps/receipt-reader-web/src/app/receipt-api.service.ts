import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { interval, switchMap, takeWhile } from 'rxjs';
import { CreateReceiptResponse, JobResponse, ReceiptListItem, ReceiptResponse } from './receipt.models';

@Injectable({ providedIn: 'root' })
export class ReceiptApiService {
  private readonly http = inject(HttpClient);
  private readonly apiBaseUrl = 'http://localhost:5186/api';

  uploadReceipt(file: File) {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<CreateReceiptResponse>(`${this.apiBaseUrl}/receipts`, formData);
  }

  listReceipts() {
    return this.http.get<ReceiptListItem[]>(`${this.apiBaseUrl}/receipts`);
  }

  getReceipt(receiptId: string) {
    return this.http.get<ReceiptResponse>(`${this.apiBaseUrl}/receipts/${receiptId}`);
  }

  getJob(jobId: string) {
    return this.http.get<JobResponse>(`${this.apiBaseUrl}/jobs/${jobId}`);
  }

  pollJob(jobId: string) {
    return interval(2500).pipe(
      switchMap(() => this.getJob(jobId)),
      takeWhile((job) => job.stage !== 'Completed' && job.stage !== 'Failed', true)
    );
  }
}
