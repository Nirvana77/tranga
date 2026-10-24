using System.Collections.Concurrent;
using System.Net;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace API.MangaDownloadClients;

/// <summary>
/// For sites where Cloudflare challenges every request (pages and images alike).
/// Solves the challenge once with FlareSolverr, then makes plain requests with the
/// cf_clearance cookie and the User-Agent it was issued for. Unlike <see cref="FlareSolverrDownloadClient"/>
/// this also works for binary content such as images.
/// The clearance is cached per site (e.g. example.com also covers img.example.com) and renewed when Cloudflare challenges again.
/// </summary>
internal class FlareSolverrClearanceDownloadClient : IDownloadClient
{
    private static readonly HttpClient Client = new(handler: Tranga.RateLimitHandler)
    {
        Timeout = TimeSpan.FromSeconds(Constants.HttpRequestTimeout),
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
    };
    private static readonly HttpClient FlareSolverrClient = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };
    private static readonly ConcurrentDictionary<string, Clearance> Clearances = new();
    private static readonly SemaphoreSlim SolveLock = new(1, 1);
    private ILog Log { get; } = LogManager.GetLogger(typeof(FlareSolverrClearanceDownloadClient));

    private record Clearance(string UserAgent, string Cookies);

    public async Task<HttpResponseMessage> MakeRequest(string url, RequestType requestType, string? referrer = null, CancellationToken? cancellationToken = null)
    {
        CancellationToken ct = cancellationToken ?? CancellationToken.None;
        string site = SiteOf(new Uri(url));
        referrer ??= $"https://{site}/";

        Clearances.TryGetValue(site, out Clearance? clearance);
        HttpResponseMessage response = await Send(url, referrer, clearance, ct);
        if (response.IsSuccessStatusCode || !IsCloudflareChallenge(response))
            return response;

        Log.DebugFormat("Cloudflare challenged {0}, solving with FlareSolverr", url);
        response.Dispose();
        if (await Solve(site, clearance, ct) is not { } newClearance)
            return new(HttpStatusCode.Forbidden);
        return await Send(url, referrer, newClearance, ct);
    }

    private async Task<HttpResponseMessage> Send(string url, string referrer, Clearance? clearance, CancellationToken ct)
    {
        HttpRequestMessage requestMessage = new(HttpMethod.Get, url);
        requestMessage.Headers.Referrer = new(referrer);
        requestMessage.Headers.TryAddWithoutValidation("User-Agent", clearance?.UserAgent ?? Tranga.Settings.UserAgent);
        if (clearance is not null)
            requestMessage.Headers.TryAddWithoutValidation("Cookie", clearance.Cookies);
        try
        {
            HttpResponseMessage response = await Client.SendAsync(requestMessage, ct);
            Log.DebugFormat("Request {0} returned {1} {2}", url, (int)response.StatusCode, response.StatusCode);
            return response;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Error(e);
            return new(HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>
    /// Gets a new clearance for <paramref name="site"/>, unless another request already renewed <paramref name="stale"/> meanwhile.
    /// </summary>
    private async Task<Clearance?> Solve(string site, Clearance? stale, CancellationToken ct)
    {
        await SolveLock.WaitAsync(ct);
        try
        {
            if (Clearances.TryGetValue(site, out Clearance? current) && current != stale)
                return current;

            if (Tranga.Settings.FlareSolverrUrl == string.Empty)
            {
                Log.Error("FlareSolverr URL is empty, can not solve the Cloudflare challenge");
                return null;
            }

            Uri flareSolverrUri = new(Tranga.Settings.FlareSolverrUrl);
            if (flareSolverrUri.Segments.Last() != "v1")
                flareSolverrUri = new UriBuilder(flareSolverrUri) { Path = "v1" }.Uri;

            JObject requestObj = new()
            {
                ["cmd"] = "request.get",
                ["url"] = $"https://{site}/",
                ["maxTimeout"] = 90000
            };
            using StringContent content = new(JsonConvert.SerializeObject(requestObj), System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await FlareSolverrClient.PostAsync(flareSolverrUri, content, ct);
            JObject responseObj = JObject.Parse(await response.Content.ReadAsStringAsync(ct));

            if (responseObj["status"]?.Value<string>() != "ok"
                || responseObj["solution"] is not JObject solution
                || solution["userAgent"]?.Value<string>() is not { } userAgent
                || solution["cookies"] is not JArray cookies)
            {
                Log.ErrorFormat("FlareSolverr could not solve {0}: {1}", site, responseObj["message"]);
                return null;
            }

            string cookieHeader = string.Join("; ", cookies.Select(c => $"{c["name"]}={c["value"]}"));
            if (!cookieHeader.Contains("cf_clearance="))
                Log.WarnFormat("FlareSolverr returned no cf_clearance cookie for {0}", site);

            Clearance clearance = new(userAgent, cookieHeader);
            Clearances[site] = clearance;
            Log.InfoFormat("Got Cloudflare clearance for {0}", site);
            return clearance;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Error(e);
            return null;
        }
        finally
        {
            SolveLock.Release();
        }
    }

    private static bool IsCloudflareChallenge(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
        && (response.Headers.Contains("cf-mitigated")
            || response.Headers.Server.Any(s => (s.Product?.Name ?? "").Contains("cloudflare", StringComparison.InvariantCultureIgnoreCase)));

    /// <summary>
    /// Registrable domain of <paramref name="uri"/>, good enough for the sites this is used with (img.example.com → example.com).
    /// </summary>
    private static string SiteOf(Uri uri) => string.Join('.', uri.Host.Split('.').TakeLast(2));
}
