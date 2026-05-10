using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace garge_api.Services
{
    public class PuppeteerPdfRenderer : IPdfRenderer
    {
        public async Task<byte[]> RenderAsync(string html)
        {
            var execPath = Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH");
            if (string.IsNullOrEmpty(execPath))
            {
                var browserFetcher = new BrowserFetcher();
                await browserFetcher.DownloadAsync();
            }

            // Sandbox is on by default. The container that hosts this service
            // must run Chrome as a non-root user (or set PUPPETEER_NO_SANDBOX=1
            // explicitly when running in a constrained environment that
            // already provides isolation).
            var disableSandbox = string.Equals(
                Environment.GetEnvironmentVariable("PUPPETEER_NO_SANDBOX"),
                "1", StringComparison.Ordinal);
            var args = new List<string> { "--disable-dev-shm-usage" };
            if (disableSandbox)
            {
                args.Add("--no-sandbox");
                args.Add("--disable-setuid-sandbox");
            }

            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = execPath,
                Args = args.ToArray()
            });
            await using var page = await browser.NewPageAsync();
            await page.SetContentAsync(html, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Load]
            });

            return await page.PdfDataAsync(new PdfOptions
            {
                Format = PaperFormat.A4,
                PrintBackground = true,
                MarginOptions = new MarginOptions { Top = "0", Bottom = "15mm", Left = "0", Right = "0" }
            });
        }
    }
}
