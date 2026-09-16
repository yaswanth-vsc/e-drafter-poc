import { HttpClient } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Observable } from 'rxjs';
import {
  Agreement, CreateAgreementInput, Quote, SpendResponse, SpendSummary, StateRules
} from './models';

const BASE = 'http://localhost:5100';

@Injectable({ providedIn: 'root' })
export class ApiService {
  /** Pushed to by SignalR when the backend advances an agreement. */
  readonly liveUpdate = signal<{ id: string; status: string; message: string } | null>(null);
  readonly connected = signal(false);

  private hub?: signalR.HubConnection;

  constructor(private http: HttpClient) {}

  // ---- Reads (all free) --------------------------------------------------

  health(): Observable<any> {
    return this.http.get(`${BASE}/api/health`);
  }

  rules(): Observable<StateRules> {
    return this.http.get<StateRules>(`${BASE}/api/rules`);
  }

  list(): Observable<Agreement[]> {
    return this.http.get<Agreement[]>(`${BASE}/api/agreements`);
  }

  get(id: string): Observable<Agreement> {
    return this.http.get<Agreement>(`${BASE}/api/agreements/${id}`);
  }

  /** The cost preview. Free — nothing is ordered and the wallet is untouched. */
  quote(id: string): Observable<Quote> {
    return this.http.get<Quote>(`${BASE}/api/agreements/${id}/quote`);
  }

  spendSummary(): Observable<SpendSummary> {
    return this.http.get<SpendSummary>(`${BASE}/api/spend/summary`);
  }

  create(input: CreateAgreementInput): Observable<Agreement> {
    return this.http.post<Agreement>(`${BASE}/api/agreements`, input);
  }

  // ---- Spending calls ----------------------------------------------------
  //
  // Neither of these is ever retried by the client. A failed request may still
  // have moved money; the backend ledger is the source of truth, not this call.

  placeOrder(id: string): Observable<SpendResponse> {
    return this.http.post<SpendResponse>(`${BASE}/api/agreements/${id}/order`, {});
  }

  /** Generates the PDF and sends it for signing in one step. Debits at link generation. */
  prepareAndSend(id: string): Observable<SpendResponse> {
    return this.http.post<SpendResponse>(`${BASE}/api/agreements/${id}/prepare-and-send`, {});
  }

  // ---- Recovery and status ----------------------------------------------

  reconcile(): Observable<any[]> {
    return this.http.post<any[]>(`${BASE}/api/reconcile`, {});
  }

  refreshStamp(id: string): Observable<{ stampReady: boolean }> {
    return this.http.post<{ stampReady: boolean }>(`${BASE}/api/agreements/${id}/refresh-stamp`, {});
  }

  refreshSigning(id: string): Observable<Agreement> {
    return this.http.post<Agreement>(`${BASE}/api/agreements/${id}/refresh-signing`, {});
  }

  pdfUrl(id: string): string {
    return `${BASE}/api/agreements/${id}/pdf`;
  }

  signedPdfUrl(id: string): string {
    return `${BASE}/api/agreements/${id}/signed-pdf`;
  }

  stampPdfUrl(id: string): string {
    return `${BASE}/api/agreements/${id}/stamp-pdf`;
  }

  // ---- Live updates ------------------------------------------------------
  //
  // The wait between ordering and the stamp arriving is measured in hours, so
  // without this the user sits refreshing a page.

  connectLive(): void {
    if (this.hub) return;

    this.hub = new signalR.HubConnectionBuilder()
      .withUrl(`${BASE}/hubs/agreements`)
      .withAutomaticReconnect()
      .build();

    this.hub.on('AgreementUpdated', (payload: any) => {
      this.liveUpdate.set(payload);
    });

    this.hub.onreconnected(() => this.connected.set(true));
    this.hub.onclose(() => this.connected.set(false));

    this.hub.start()
      .then(() => this.connected.set(true))
      .catch(() => this.connected.set(false));
  }
}
