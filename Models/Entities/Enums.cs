namespace CoreBankingSolution.Models.Entities;

public enum GlType { Asset = 1, Liability = 2, Equity = 3, Income = 4, Expense = 5, Contingent = 6 }

public enum ProductCategory { Savings = 1, Current = 2, SND = 3, FDR = 4, DPS = 5, TermLoan = 10, DemandLoan = 11 }

public enum PayFrequency { None = 0, Monthly = 1, Quarterly = 3, HalfYearly = 6, Yearly = 12 }

public enum ApprovalStatus { Pending = 0, Approved = 1, Rejected = 2 }

public enum CustomerType
{
    Individual = 1, Joint = 2, Minor = 3, Staff = 4, NonResident = 5, Proprietary = 6, Partnership = 7,
    PrivateLtd = 8, PublicLtd = 9, Government = 10, Autonomous = 11, SemiAutonomous = 12, LocalAuthority = 13,
    ClubSocietyAssociation = 14, NonProfitOrganization = 15, NonGovtOrganization = 16, Trustee = 17,
    Liquidator = 18, OtherBank = 19, Other = 20
}

public enum RiskRating { Low = 1, Medium = 2, High = 3 }

public enum KycStatus { Pending = 0, Verified = 1, Rejected = 2 }

public enum AccountStatus { PendingApproval = 0, Active = 1, Dormant = 2, Inoperative = 3, Frozen = 4, Blocked = 5, Closed = 6, Matured = 7 }

public enum OperationMode { Single = 1, JointAny = 2, JointAll = 3, EitherOrSurvivor = 4 }

public enum VoucherType
{
    CashDeposit = 1, CashWithdrawal = 2, Transfer = 3, Journal = 4, Charge = 5, FdrOpen = 6, Encash = 7,
    LoanDisburse = 8, LoanRepay = 9, RemittanceIssue = 10, RemittancePay = 11, InterestPosting = 12,
    Clearing = 13, AccountOpening = 14, Reversal = 15, Eod = 16, StandingInstruction = 17, DpsInstallment = 18
}

public enum VoucherStatus { Pending = 0, Authorized = 1, Rejected = 2, Reversed = 3 }

public enum LoanStatus { Applied = 0, Approved = 1, Disbursed = 2, Closed = 3, Rejected = 4 }

public enum LoanClass { Standard = 0, SMA = 1, SubStandard = 2, Doubtful = 3, BadLoss = 4 }

public enum InstallmentStatus { Due = 0, Paid = 1 }

public enum RemittanceType { PO = 1, DD = 2, TT = 3 }

public enum RemittanceStatus { Issued = 0, Paid = 1, Cancelled = 2, Stopped = 3 }

public enum ClearingDirection { Outward = 1, Inward = 2 }

public enum ClearingStatus { Registered = 0, Settled = 1, Returned = 2 }

public enum SiFrequency { Daily = 1, Weekly = 7, Monthly = 30, Quarterly = 90, HalfYearly = 180, Yearly = 365 }

public enum SiStatus { Active = 0, Paused = 1, Completed = 2, Cancelled = 3 }

public enum EodStatus { Started = 0, Success = 1, Failed = 2, Info = 3 }

public enum SanctionSource { UNSCR = 1, Local = 2 }
