export interface Party {
  name: string;
  email: string;
  phone: string;
}

export interface Signatory {
  name: string;
  email: string;
  phone: string;
  status: string;
  signUrl: string | null;
  signedAt: string | null;
}

export interface Agreement {
  id: string;
  refId: string;
  status: AgreementStatus;
  firstParty: Party;
  secondParty: Party;
  propertyAddress: string;
  considerationAmount: number;
  monthlyRent: number;
  leaseTermMonths: number;
  leaseStartDate: string;
  denomination: number;
  orderIdd: number | null;
  orderDisplayId: string | null;
  certificateNo: string | null;
  esignDocumentId: string | null;
  esignCost: number | null;
  signatories: Signatory[];
  createdAt: string;
  updatedAt: string | null;
}

export type AgreementStatus =
  | 'Draft'
  | 'OrderPlaced'
  | 'OrderProcessing'
  | 'StampReady'
  | 'Preparing'
  | 'SentForSigning'
  | 'PartiallySigned'
  | 'Signed'
  | 'Failed'
  | 'NeedsReview'
  | 'Cancelled';

export interface CreateAgreementInput {
  firstPartyName: string;
  firstPartyEmail: string;
  firstPartyPhone: string;
  secondPartyName: string;
  secondPartyEmail: string;
  secondPartyPhone: string;
  propertyAddress: string;
  considerationAmount: number;
  monthlyRent: number;
  leaseTermMonths: number;
  leaseStartDate: string;
}

export interface Quote {
  duty: {
    article: string;
    consideration: number;
    pct: number | null;
    cap: number | null;
    amount: number;
    wasCapped: boolean;
    warning: string | null;
  };
  order: {
    stampValue: number;
    serviceCharge: number;
    shipping: number;
    gst: number;
    total: number;
  };
  esign: {
    signMethod: string;
    signatories: number;
    ratePerSignatory: number;
    total: number;
    gst: string;
    /** The non-refundability warning, written by the backend. Display it verbatim. */
    confirmation: string;
  };
  grandTotal: number;
  walletBalance: number;
  affordable: boolean;
}

/**
 * Every spending call returns one of these. `unknown` is deliberately NOT an error:
 * the money may have moved, and the only correct response is to reconcile, never retry.
 */
export type SpendStatus =
  | 'succeeded'
  | 'failed_safe'
  | 'unknown'
  | 'already_attempted'
  | 'blocked';

export interface SpendResponse<T = any> {
  status: SpendStatus;
  result?: T;
  message?: string;
  note?: string;
  attemptId?: number;
  existingStatus?: string;
}

export interface SpendSummary {
  mode: string;
  armed: boolean;
  totalSpentPaise: number;
  ceilingPaise: number;
  succeededCount: number;
  unknownCount: number;
  needsReviewCount: number;
}

export interface StateRules {
  state: string;
  article: string;
  rules: {
    denominationConstraint: { min: number | null; max: number | null; label: string } | null;
    denominationHint: string | null;
    govSurchargePct: number;
    fields: {
      maxFieldLength: number;
      disallowSpecialChars: boolean;
      disallowNumericInNames: boolean;
      considerationPriceAllowed: boolean;
      secondPartyNameRequired: boolean;
      purchaserOnly: boolean;
    };
  };
}
