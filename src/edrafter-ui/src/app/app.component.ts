import { CommonModule } from '@angular/common';
import { Component, OnInit, effect, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ApiService } from './api.service';
import { Agreement, Quote, SpendResponse, SpendSummary, StateRules } from './models';

type Step = 'form' | 'review' | 'tracking';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent implements OnInit {
  private api = inject(ApiService);
  private fb = inject(FormBuilder);

  step = signal<Step>('form');
  busy = signal(false);
  error = signal<string | null>(null);

  rules = signal<StateRules | null>(null);
  agreement = signal<Agreement | null>(null);
  quote = signal<Quote | null>(null);
  spend = signal<SpendSummary | null>(null);
  agreements = signal<Agreement[]>([]);

  /** Set when a spend returns `unknown` — the case that must never be retried. */
  unknownOutcome = signal<SpendResponse | null>(null);

  confirmOrder = signal(false);
  confirmSend = signal(false);

  liveMessage = signal<string | null>(null);
  connected = this.api.connected;

  form = this.fb.group({
    firstPartyName: ['', [Validators.required, Validators.maxLength(50)]],
    firstPartyEmail: ['', [Validators.required, Validators.email]],
    firstPartyPhone: ['', [Validators.required, Validators.pattern(/^\d{10}$/)]],
    secondPartyName: ['', [Validators.required, Validators.maxLength(50)]],
    secondPartyEmail: ['', [Validators.required, Validators.email]],
    secondPartyPhone: ['', [Validators.required, Validators.pattern(/^\d{10}$/)]],
    propertyAddress: ['', [Validators.required, Validators.maxLength(250)]],
    considerationAmount: [100000, [Validators.required, Validators.min(1)]],
    monthlyRent: [10000, [Validators.required, Validators.min(1)]],
    leaseTermMonths: [11, [Validators.required, Validators.min(1), Validators.max(12)]],
    leaseStartDate: [new Date().toISOString().slice(0, 10), Validators.required]
  });

  constructor() {
    // A live update for the agreement on screen refreshes it in place.
    effect(() => {
      const update = this.api.liveUpdate();
      if (!update) return;
      this.liveMessage.set(update.message);
      const current = this.agreement();
      if (current && update.id === current.id) {
        this.api.get(current.id).subscribe(a => this.agreement.set(a));
      }
      this.loadList();
    });
  }

  ngOnInit(): void {
    this.api.connectLive();
    this.api.rules().subscribe({
      next: r => this.rules.set(r),
      error: () => this.error.set('Cannot reach the API. Is it running on :5100?')
    });
    this.loadSpend();
    this.loadList();
  }

  // ---- Derived display values -------------------------------------------

  /**
   * Duty preview shown live as the user types, so the cost is never a surprise
   * at the confirmation step. Mirrors the backend rule: 0.5%, capped at 500 for
   * this article. The backend remains the authority.
   */
  get estimatedDuty(): number {
    const consideration = Number(this.form.value.considerationAmount ?? 0);
    const raw = consideration * 0.005;
    return Math.min(raw, 500);
  }

  get dutyIsCapped(): boolean {
    const consideration = Number(this.form.value.considerationAmount ?? 0);
    return consideration * 0.005 > 500;
  }

  loadSpend(): void {
    this.api.spendSummary().subscribe(s => this.spend.set(s));
  }

  loadList(): void {
    this.api.list().subscribe(a => this.agreements.set(a));
  }

  // ---- Flow --------------------------------------------------------------

  submitForm(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.api.create(this.form.value as any).subscribe({
      next: a => {
        this.agreement.set(a);
        this.loadQuote(a.id);
        this.step.set('review');
        this.busy.set(false);
        this.loadList();
      },
      error: e => {
        this.error.set(e?.error?.error ?? 'Could not create the draft.');
        this.busy.set(false);
      }
    });
  }

  loadQuote(id: string): void {
    this.api.quote(id).subscribe({
      next: q => this.quote.set(q),
      error: () => this.error.set('Could not fetch the quote.')
    });
  }

  /** 💸 Irreversible. Only reachable after the confirmation checkbox. */
  placeOrder(): void {
    const a = this.agreement();
    if (!a || !this.confirmOrder()) return;

    this.busy.set(true);
    this.error.set(null);

    this.api.placeOrder(a.id).subscribe({
      next: res => this.handleSpend(res, a.id),
      error: err => this.handleSpendError(err, a.id)
    });
  }

  /** 💸 Irreversible and non-refundable. Debits at link generation, per signatory. */
  sendForSigning(): void {
    const a = this.agreement();
    if (!a || !this.confirmSend()) return;

    this.busy.set(true);
    this.error.set(null);

    this.api.prepareAndSend(a.id).subscribe({
      next: res => this.handleSpend(res, a.id),
      error: err => this.handleSpendError(err, a.id)
    });
  }

  private handleSpend(res: SpendResponse, id: string): void {
    this.busy.set(false);

    if (res.status === 'succeeded') {
      this.api.get(id).subscribe(a => this.agreement.set(a));
      this.step.set('tracking');
      this.loadSpend();
      this.loadList();
      return;
    }

    // `unknown` is not a failure. Surface it as its own state with the
    // reconcile action, and never offer a retry button.
    if (res.status === 'unknown') {
      this.unknownOutcome.set(res);
      this.step.set('tracking');
      return;
    }

    this.error.set(res.message ?? 'The request was refused.');
  }

  private handleSpendError(err: any, id: string): void {
    this.busy.set(false);
    const body = err?.error as SpendResponse | undefined;

    if (body?.status === 'unknown') {
      this.unknownOutcome.set(body);
      this.step.set('tracking');
      return;
    }

    if (body?.status === 'already_attempted') {
      // The guard did its job. Show what actually happened rather than an error.
      this.error.set(body.message ?? 'Already attempted; not charged again.');
      this.api.get(id).subscribe(a => this.agreement.set(a));
      return;
    }

    this.error.set(body?.message ?? 'The request failed.');
  }

  reconcile(): void {
    this.busy.set(true);
    this.api.reconcile().subscribe({
      next: results => {
        this.busy.set(false);
        this.unknownOutcome.set(null);
        const a = this.agreement();
        if (a) this.api.get(a.id).subscribe(x => this.agreement.set(x));
        this.loadSpend();
        this.liveMessage.set(
          results.length
            ? results.map(r => r.message).join(' · ')
            : 'Nothing needed reconciling.'
        );
      },
      error: () => {
        this.busy.set(false);
        this.error.set('Reconciliation failed.');
      }
    });
  }

  refreshStamp(): void {
    const a = this.agreement();
    if (!a) return;
    this.busy.set(true);
    this.api.refreshStamp(a.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.api.get(a.id).subscribe(x => this.agreement.set(x));
      },
      error: () => this.busy.set(false)
    });
  }

  refreshSigning(): void {
    const a = this.agreement();
    if (!a) return;
    this.busy.set(true);
    this.api.refreshSigning(a.id).subscribe({
      next: x => {
        this.busy.set(false);
        this.agreement.set(x);
      },
      error: () => this.busy.set(false)
    });
  }

  open(a: Agreement): void {
    this.agreement.set(a);
    this.unknownOutcome.set(null);
    if (a.status === 'Draft') {
      this.loadQuote(a.id);
      this.step.set('review');
    } else {
      this.step.set('tracking');
    }
  }

  startNew(): void {
    this.agreement.set(null);
    this.quote.set(null);
    this.unknownOutcome.set(null);
    this.confirmOrder.set(false);
    this.confirmSend.set(false);
    this.error.set(null);
    this.step.set('form');
  }

  // ---- View helpers ------------------------------------------------------

  pdfUrl(id: string): string { return this.api.pdfUrl(id); }
  signedPdfUrl(id: string): string { return this.api.signedPdfUrl(id); }
  stampPdfUrl(id: string): string { return this.api.stampPdfUrl(id); }

  /**
   * True once the e-stamp exists. Deliberately NOT keyed on certificateNo: eDrafter
   * returns that as an empty string even for a completed order, so the timeline would
   * sit on "waiting" forever after the stamp had arrived.
   */
  stampIssued(a: Agreement): boolean {
    return ['StampReady', 'Preparing', 'SentForSigning',
            'PartiallySigned', 'Signed'].includes(a.status);
  }

  statusClass(status: string): string {
    switch (status) {
      case 'Signed': return 'ok';
      case 'Failed': case 'NeedsReview': return 'bad';
      case 'Draft': return 'muted';
      default: return 'pending';
    }
  }

  fieldInvalid(name: string): boolean {
    const c = this.form.get(name);
    return !!c && c.invalid && c.touched;
  }
}
