using System.Globalization;
using System.Text;
using garge_api.Dtos.Admin;
using garge_api.Models;
using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Controllers
{
    [ApiController]
    [Route("api/admin/vat")]
    [Authorize(Policy = "Admin")]
    public class VatController(
        ApplicationDbContext db,
        IVatThresholdService vat,
        IInvoiceService invoices,
        IAppSettingsCache settingsCache) : ControllerBase
    {
        /// <summary>
        /// Turnover against the VAT registration threshold, with the months it covers, and the sales that owe
        /// VAT from before registration.
        /// </summary>
        [HttpGet("threshold")]
        public async Task<ActionResult<VatThresholdDto>> GetThreshold(CancellationToken ct) =>
            ToDto(await vat.GetStatusAsync(DateTime.UtcNow, ct));

        /// <summary>
        /// Sets the sales made outside garge-api in one month that has started, and answers the threshold
        /// as it then stands. Zero removes the month.
        /// </summary>
        [HttpPut("outside-sales")]
        public async Task<ActionResult<VatThresholdDto>> SetOutsideSales([FromBody] UpdateOutsideSalesDto dto, CancellationToken ct)
        {
            if (!UpdateOutsideSalesDto.TryParseMonth(dto.Month, out var month))
            {
                ModelState.AddModelError(nameof(UpdateOutsideSalesDto.Month), "Use a month as yyyy-MM.");
                return ValidationProblem(ModelState);
            }
            var now = DateTime.UtcNow;
            if (!await vat.SetOutsideSalesAsync(month, dto.AmountInOre, now, ct))
            {
                ModelState.AddModelError(nameof(UpdateOutsideSalesDto.Month), "The month has not started yet.");
                return ValidationProblem(ModelState);
            }
            return ToDto(await vat.GetStatusAsync(now, ct));
        }

        private static VatThresholdDto ToDto(VatThresholdStatus status) => new()
        {
            TurnoverInOre = status.TurnoverInOre,
            ThresholdInOre = status.ThresholdInOre,
            VatEnabled = status.VatEnabled,
            CrossingInvoiceId = status.CrossingInvoiceId,
            CrossedAt = status.CrossedAt,
            OwedVatInOre = status.Owed.Sum(o => (long)o.VatInOre),
            Owed = status.Owed.Select(o => new VatOwedSaleDto
            {
                InvoiceId = o.InvoiceId, IssuedAt = o.IssuedAt, AmountInOre = o.AmountInOre,
                VatInOre = o.VatInOre, CorrectedAt = o.CorrectedAt,
                CreditNoteId = o.CreditNoteId, ReplacementInvoiceId = o.ReplacementInvoiceId
            }).ToList(),
            Months = status.Months.Select(m => new VatMonthDto
            {
                Month = m.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                SalesInOre = m.SalesInOre,
                OutsideInOre = m.OutsideInOre
            }).ToList()
        };

        /// <summary>
        /// For each sale that owes VAT from before registration, makes a credit note and a new invoice
        /// with VAT and emails both to the customer. Requires VAT on.
        /// </summary>
        [HttpPost("corrections")]
        public async Task<IActionResult> MakeCorrections(CancellationToken ct)
        {
            if (!(await settingsCache.GetAsync()).VatEnabled)
                return BadRequest("Turn VAT on after registration before making VAT corrections.");
            return Ok(new { made = await invoices.GenerateVatCorrectionsAsync(ct) });
        }

        /// <summary>Downloads a credit note or replacement invoice made by a VAT correction.</summary>
        [HttpGet("documents/{invoiceId:int}")]
        public async Task<IActionResult> GetDocument(int invoiceId, CancellationToken ct)
        {
            var document = await db.Invoices.AsNoTracking()
                .Where(i => i.Id == invoiceId && (i.CreditsInvoiceId != null || i.ReplacesInvoiceId != null))
                .Select(i => new { i.PdfData, i.Kind })
                .FirstOrDefaultAsync(ct);
            if (document is not { PdfData.Length: > 0 }) return NotFound();
            var name = document.Kind == InvoiceKind.CreditNote ? "credit-note" : "invoice";
            return File(document.PdfData, "application/pdf", $"{name}-{invoiceId:D4}.pdf");
        }

        /// <summary>The sales that owe VAT from before registration, as CSV for the VAT return.</summary>
        [HttpGet("owed.csv")]
        public async Task<IActionResult> GetOwedCsv(CancellationToken ct)
        {
            var status = await vat.GetStatusAsync(DateTime.UtcNow, ct);
            var csv = new StringBuilder("invoice;date;amount_nok;excl_vat_nok;vat_nok;credit_note;new_invoice\n");
            foreach (var o in status.Owed)
            {
                var exclVat = o.AmountInOre - o.VatInOre;
                csv.Append(CultureInfo.InvariantCulture,
                    $"{o.InvoiceId};{LocalTime.Date(o.IssuedAt):yyyy-MM-dd};{o.AmountInOre / 100m:0.00};{exclVat / 100m:0.00};{o.VatInOre / 100m:0.00};{o.CreditNoteId};{o.ReplacementInvoiceId}\n");
            }
            return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "vat-owed.csv");
        }
    }
}
