using garge_api.Models.Admin;
using System.Web;

namespace garge_api.Services
{
    /// <summary>
    /// Shared shell for transactional emails (and the invoice PDF). Renders the
    /// dark header band, brand info, a configurable right-side meta block, the
    /// caller's body slot, and the company footer. Both InvoiceService and
    /// OrderEmailService use this so theme changes live in one place.
    /// </summary>
    public static class EmailLayout
    {
        public sealed class Meta
        {
            public string? Number { get; set; }      // e.g. "#0007"
            public string? Subtitle { get; set; }    // e.g. "INVOICE · 2026-05-07"
            public string? Badge { get; set; }       // e.g. "Paid"
            public string? FootNote { get; set; }    // e.g. "Vipps order #7"
        }

        public static string Render(AppSettings s, Meta? meta, string bodyHtml)
        {
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);

            var orgLine = s.VatEnabled ? $"{s.CompanyOrgNumber} MVA" : s.CompanyOrgNumber;

            var metaBlock = string.Empty;
            if (meta != null)
            {
                var number = string.IsNullOrEmpty(meta.Number) ? string.Empty
                    : $"""<div class="meta-number">{H(meta.Number)}</div>""";
                var subtitle = string.IsNullOrEmpty(meta.Subtitle) ? string.Empty
                    : $"""<div class="meta-subtitle">{H(meta.Subtitle)}</div>""";
                var badge = string.IsNullOrEmpty(meta.Badge) ? string.Empty
                    : $"""<span class="badge">{H(meta.Badge)}</span>""";
                var foot = string.IsNullOrEmpty(meta.FootNote) ? string.Empty
                    : $"""<div class="meta-foot">{H(meta.FootNote)}</div>""";
                metaBlock = $"""<div class="meta">{number}{subtitle}{badge}{foot}</div>""";
            }

            return $$"""
                <!DOCTYPE html>
                <html>
                <head>
                <meta charset="utf-8">
                <style>
                  * { box-sizing: border-box; margin: 0; padding: 0; }
                  body { font-family: Arial, Helvetica, sans-serif; font-size: 12px; color: #1a1a1a; background: #fff; }

                  .header-band {
                    background: #182232;
                    color: #fff;
                    padding: 28px 32px 20px;
                    display: flex;
                    justify-content: space-between;
                    align-items: flex-start;
                  }
                  .brand { font-size: 22px; font-weight: 700; letter-spacing: .04em; margin-bottom: 4px; }
                  .brand-sub { font-size: 11px; opacity: .85; line-height: 1.5; }
                  .meta { text-align: right; }
                  .meta-number { font-size: 28px; font-weight: 700; line-height: 1; margin-bottom: 4px; }
                  .meta-subtitle { font-size: 11px; opacity: .85; margin-bottom: 6px; }
                  .meta-foot { margin-top: 6px; font-size: 10px; opacity: .75; }
                  .badge {
                    display: inline-block;
                    background: rgba(255,255,255,0.25);
                    border: 1px solid rgba(255,255,255,0.5);
                    color: #fff;
                    padding: 2px 10px;
                    border-radius: 20px;
                    font-size: 10px;
                    font-weight: 700;
                    letter-spacing: .08em;
                    text-transform: uppercase;
                  }

                  .body { padding: 24px 32px; }
                  .body p { line-height: 1.6; color: #333; }
                  .body h1, .body h2 { color: #1a1a1a; margin-bottom: 8px; }
                  .body h1 { font-size: 18px; }

                  .parties { display: flex; gap: 40px; margin-bottom: 24px; }
                  .party { flex: 1; }
                  .party-label {
                    font-size: 9px; font-weight: 700; text-transform: uppercase;
                    letter-spacing: .1em; color: #0284c7; margin-bottom: 6px;
                  }

                  table { width: 100%; border-collapse: collapse; }
                  th {
                    font-size: 10px; font-weight: 700; text-transform: uppercase;
                    letter-spacing: .06em; color: #0284c7;
                    border-bottom: 2px solid #0284c7;
                    padding: 7px 8px; text-align: left;
                  }
                  td { padding: 7px 8px; border-bottom: 1px solid #e5e7eb; }
                  tr:last-child td { border-bottom: none; }
                  tbody tr:nth-child(even) { background: #f8fafc; }
                  .r { text-align: right; }
                  .c { text-align: center; }

                  .totals-section { margin-top: 8px; border-top: 2px solid #e5e7eb; padding-top: 8px; }
                  .totals-section table { width: 40%; margin-left: auto; }
                  .sub td { padding: 3px 8px; color: #555; }
                  .grand td {
                    padding: 7px 8px; font-weight: 700; font-size: 14px;
                    border-top: 2px solid #0284c7; color: #0284c7;
                  }

                  .footer {
                    margin: 24px 32px 0;
                    padding-top: 12px;
                    border-top: 1px solid #e5e7eb;
                    font-size: 10px; color: #888; line-height: 1.6;
                  }
                  .footer p + p { margin-top: 2px; }
                </style>
                </head>
                <body>

                <div class="header-band">
                  <div>
                    <div class="brand">{{H(s.CompanyName)}}</div>
                    <div class="brand-sub">
                      {{H(s.CompanyLegalName)}}<br>
                      Org. no. {{H(orgLine)}}<br>
                      {{H(s.CompanyAddress)}}<br>
                      {{H(s.CompanyEmail)}}
                    </div>
                  </div>
                  {{metaBlock}}
                </div>

                <div class="body">
                {{bodyHtml}}
                </div>

                </body>
                </html>
                """;
        }
    }
}
