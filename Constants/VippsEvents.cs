namespace garge_api.Constants
{
    public static class VippsEvents
    {
        public const string PaymentAuthorized = "epayments.payment.authorized.v1";
        public const string PaymentCaptured   = "epayments.payment.captured.v1";
        public const string PaymentRefunded   = "epayments.payment.refunded.v1";
        public const string PaymentTerminated = "epayments.payment.terminated.v1";
        public const string PaymentAborted    = "epayments.payment.aborted.v1";
        public const string PaymentExpired    = "epayments.payment.expired.v1";
        public const string PaymentCancelled  = "epayments.payment.cancelled.v1";
        public const string PaymentCreated    = "epayments.payment.created.v1";

        public const string AgreementActivated = "recurring.agreement-activated.v1";
        public const string AgreementStopped   = "recurring.agreement-stopped.v1";
        public const string AgreementExpired   = "recurring.agreement-expired.v1";
        public const string AgreementRejected  = "recurring.agreement-rejected.v1";
        public const string ChargeCaptured     = "recurring.charge-captured.v1";
        public const string ChargeFailed       = "recurring.charge-failed.v1";
        public const string ChargeCreationFailed = "recurring.charge-creation-failed.v1";

        public static readonly string[] ShopEvents =
        {
            PaymentAuthorized, PaymentCaptured, PaymentRefunded, PaymentTerminated,
            PaymentAborted, PaymentExpired, PaymentCancelled
        };

        public static readonly string[] SubscriptionEvents =
        {
            AgreementActivated, AgreementStopped, AgreementExpired, AgreementRejected,
            ChargeCaptured, ChargeFailed, ChargeCreationFailed
        };
    }
}
