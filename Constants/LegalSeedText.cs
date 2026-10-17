using SjolystInnovation.AppKit.SiteSettings;

namespace garge_api.Constants
{
    /// <summary>
    /// Starting text for the legal pages, written once while a page has none. It is the text garge-app
    /// showed before the pages moved here. An admin edits the pages afterwards, and the seeder never
    /// overwrites a page that exists.
    /// </summary>
    public static class LegalSeedText
    {
        /// <summary>Garge writes its legal pages in English only.</summary>
        public const string Locale = "en";

        public static readonly LegalPageSeed[] All =
        [
            new("terms", Locale, "Terms of Service", Terms),
            new("privacy", Locale, "Privacy Policy", Privacy),
            new("cookies", Locale, "Cookie Policy", Cookies),
        ];

        /// <summary>The options for <c>MapAppKitLegalPages</c>: English only, and editing needs the Admin policy.</summary>
        public static void Configure(AppKitLegalPagesOptions options)
        {
            options.Locales = [Locale];
            options.Policy = RoleNames.Admin;
        }

        private const string Terms = """
## 1. About us (seller)

The seller is:

- **Company name:** Sjølyst Innovation AS
- **Organisation number:** 938 517 789
- **Address:** Mårvegen 21a, 4347 Lye
- **Email:** sondresjoelyst@gmail.com

By placing an order or activating a subscription you confirm that you are a private consumer and that you have read and accepted these terms.

## 2. Acceptance of terms

By using Garge you agree to these terms. If you do not agree, please do not use the service.

## 3. Use of the service

Garge provides a platform for monitoring sensor data from devices registered to your account. You are responsible for keeping your login credentials secure.

## 4. Device ownership

You may only register devices you own or have been given explicit permission to register. Registering a device does not transfer hardware ownership.

## 5. Data and privacy

Sensor readings are stored and associated with your account. We do not sell your data to third parties. See our [Privacy Policy](/privacy) for full details on how we collect, use, and protect your personal data.

## 6. Availability

We aim for high availability but do not guarantee uninterrupted access. The service may be updated or taken offline for maintenance at any time.

## 7. Subscription service

A Garge subscription gives you access to all platform features: live sensor monitoring, automation rules, socket control, push notifications, and historical data. Without an active subscription you can log in but cannot access sensor data or other features.

Subscriptions are available on a monthly or yearly basis. Access begins immediately upon confirmation in Vipps.

## 8. Subscription pricing and billing

Prices are shown in NOK. Recurring charges are processed through Vipps at the start of each billing period.

We may change the price of a subscription. A higher price applies to you no earlier than 30 days after we have emailed you about it. The email states your current price, the new price and the date from which charges use the new price. Charges due before that date keep your current price.

When the price goes up, you may cancel the subscription at no cost from the Billing page, both before and after the new price takes effect. If the new amount is above the maximum you approved in Vipps, the Vipps app asks you to approve it at the first charge. A lower price applies at once.

Your current plan and next charge date are shown on the [Billing page](/profile/billing).

## 9. Subscription cancellation

You may cancel your subscription at any time from the Billing page. Cancellation takes effect at the end of the current billing period — you retain full access until that date and will not be charged again. No partial refunds are issued for unused time in the current period.

## 10. Right of withdrawal — subscription

Under the Norwegian Right of Withdrawal Act (angrerettloven §22 letter n), consumers generally have a 14-day right of withdrawal from digital service agreements. Because Garge subscriptions grant immediate access upon activation, we ask you to explicitly waive this right during checkout. By checking the confirmation box at checkout you acknowledge that the service has started and that the right of withdrawal is waived accordingly.

If you prefer not to waive this right, do not activate the service during the 14-day period and [contact us](/contact) to cancel before first use.

## 11. Physical products — purchase agreement

A binding purchase agreement for physical products is formed when you receive an order confirmation. Payment is reserved via Vipps at checkout and only captured when we ship your order — you are not charged before dispatch.

We reserve the right to cancel or refuse an order in whole or in part for any reason, including but not limited to: the item being out of stock or discontinued; a pricing or product description error; inability to verify payment; suspected fraudulent or abusive activity; or other circumstances that make fulfilment impossible or unlawful. If we cancel your order we will notify you promptly and issue a full refund of any reserved or captured amount.

## 12. Pricing

All prices are total prices in NOK. When we are registered for VAT, they include 25% VAT. No additional fees are added beyond the total shown at the time of purchase.

## 13. Delivery

We aim to ship physical orders within 5 business days. Delivery will always occur within 30 days of the order date unless otherwise agreed in writing. If we cannot deliver within 30 days we will notify you promptly, and you may cancel the order for a full refund. Risk of loss transfers to you once the goods are delivered to your address.

## 14. Right of withdrawal — physical goods

You have a 14-day right of withdrawal for physical products under the Norwegian Right of Withdrawal Act (angrerettloven), starting from the day you receive the item. You do not need to give a reason. To exercise this right, [contact us](/contact) within 14 days of receipt. Return shipping costs are at your expense. We will issue a full refund within 14 days of receiving the returned goods in acceptable condition.

## 15. Defects and complaints

Physical products carry a statutory complaints period of at least 2 years from delivery under the Norwegian Consumer Purchases Act (forbrukerkjøpsloven). For goods expected to last significantly longer this period may extend to 5 years. If your product has a defect, [contact us](/contact) as soon as possible after discovery — a complaint must be raised within a reasonable time (generally within 2 months of discovering the defect) and no later than the end of the applicable complaints period. We will offer repair, replacement, or a refund as appropriate.

## 16. Non-payment

If a subscription payment fails, access may be suspended until the outstanding amount is settled. If payment is reversed fraudulently, Garge reserves the right to recover the outstanding amount through appropriate means.

## 17. Dispute resolution

If a complaint cannot be resolved directly with us, you may contact the Norwegian Consumer Authority (Forbrukertilsynet) or the Consumer Council of Norway (Forbrukerrådet) for assistance. EU-based consumers may also use the European Commission's Online Dispute Resolution platform at [ec.europa.eu/consumers/odr](https://ec.europa.eu/consumers/odr). Norwegian law applies to these terms.

## 18. Changes to terms

We may update these terms from time to time. We will notify you of material changes by email. Continued use of Garge after notification constitutes acceptance of the updated terms.

## 19. Contact

Questions about these terms? [Contact us](/contact).
""";

        private const string Privacy = """
## What data we collect

- Account information: name, email address, phone number, hashed password, terms-acceptance record.
- Sensor data: continuous readings (temperature, humidity, voltage, switch state) sent by your registered devices, with timestamps. This is recorded every few seconds while your devices are online.
- Subscription & billing: subscription agreement ID, billing address, consent timestamp, and a truncated IP address (last octet zeroed) used as proof of consent.
- Usage data: basic server logs (opaque user IDs, request paths) for debugging and security purposes.

## How we use your data

- To provide the Garge dashboard and historical data views.
- To send account-related emails (email confirmation, password reset).
- To improve the service and diagnose technical issues.
- To improve our algorithms (such as battery-health analysis) using anonymized data that can no longer be linked to you.

## Legal basis for processing

We process your personal data under the following legal bases (GDPR Article 6):

- **Contract (Art. 6(1)(b))** — account data, sensor data, and transactional emails are necessary to deliver the service you signed up for.
- **Legitimate interests (Art. 6(1)(f))** — server logs are retained for security monitoring and debugging; readings of devices you still own are kept while you own them — even while suspended — so you keep your full history and can compare it year over year when you return; and fully anonymized readings (no link to you or your device) are kept to improve the service and its algorithms. Our interest does not override your rights — you can object at any time and switch this off (see [your profile](/profile)), and you can export or delete the underlying data at any time before it is anonymized.

## Data sharing

We do not sell or trade your personal data. The Garge platform is self-hosted on infrastructure under our direct control; there is no upstream cloud or hosting provider. We use the following third-party data processors:

- [Brevo](https://www.brevo.com/legal/termsofuse/) (France, EEA) — transactional email delivery (email confirmations, password resets, invoice receipts). Your email address and the email body are shared with Brevo solely for this purpose.
- [Vipps Mobilepay AS](https://vipps.no) (Norway, EEA) — payment processing, recurring subscription agreements, and capture of your billing address used on invoices. We never see or store your card details.

We may also disclose data when required by law.

## Data retention

- **Account & preferences** — until you delete your account, after which your name, email, phone and password are scrubbed.
- **Sensor & switch data** — kept for as long as you own the device. If you cancel or downgrade and a device exceeds your capacity, we suspend your access to it but keep recording its readings so that, when you return — for example after a seasonal break — your full history is intact and you can compare it year over year. **If you turn off retention** for your data (from your [profile](/profile)) and then have no active subscription, your suspended devices are removed from your account after 6 months and their readings are permanently deleted or irreversibly anonymized (stripped of any link to you or the device) and kept only as anonymized data to improve the service. You can remove a device or export its data at any time. When you sell or unclaim a device, or delete your account, the same applies: your readings are anonymized or deleted, never kept linked to you and never visible to a later owner.
- **Invoices & orders** — retained for 5 years after the last invoice as required by the Norwegian Bookkeeping Act (bokføringsloven §13).
- **Server logs** — 90 days. **Metrics** — 60 days.
- **Database backups** — 3 daily, 4 weekly, 6 monthly snapshots, encrypted at rest. After account deletion, residual data may persist in backups for up to about 6 months until rotation completes; backups are not used for any processing.

You can delete your account at any time from your [profile page](/profile).

## Your rights

Under GDPR you have the right to:

- **Access & portability** — download a copy of all your personal data in JSON format from your [profile page](/profile).
- **Erasure** — permanently delete your account and all associated data from your [profile page](/profile).
- **Correction** — update your name and phone number from your [profile page](/profile). For email changes, [contact us](/contact).
- **Object to retention** — turn off keeping your sensor history after your subscription ends, from your [profile page](/profile). Once off, suspended devices are removed and their data deleted or anonymized 6 months after your subscription lapses.

Privacy questions and data subject requests: [sondresjoelyst@gmail.com](mailto:sondresjoelyst@gmail.com).

## Cookies

We use cookies for authentication and session management. See our [Cookie Policy](/cookies) for details.
""";

        private const string Cookies = """
## What are cookies?

Cookies are small text files stored in your browser. They help the site remember your session and preferences between visits.

## Cookies we use

| Name | Purpose | Duration |
| --- | --- | --- |
| `next-auth.session-token` | Keeps you signed in | Session / 30 days |
| `next-auth.csrf-token` | Security — prevents cross-site request forgery | Session |
| `next-auth.callback-url` | Remembers where to redirect after login | Session |

## Third-party cookies

We do not use any third-party analytics, advertising, or tracking cookies.

## Managing cookies

You can clear or block cookies through your browser settings. Note that blocking session cookies will prevent you from staying signed in.

## Questions?

[Contact us](/contact) if you have any questions about how we use cookies.
""";
    }
}
