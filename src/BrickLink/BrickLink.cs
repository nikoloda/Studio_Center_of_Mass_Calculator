using System.Globalization;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace StudioCenterOfMass;

// =========================================================================
// BrickLink Catalog API
// =========================================================================
// Docs: https://www.bricklink.com/v3/api.page
// Every request must be signed with OAuth 1.0 (HMAC-SHA1).
// We call GET /items/PART/{partNumber} to get weight and dimensions.
// =========================================================================

// "sealed class" = cannot be inherited. Primary constructor (creds) sets a field automatically.
sealed class BrickLinkApi(BrickLinkCredentials creds)
{
    // LDraw length units (LDU). BrickLink "Stud Dim." maps to these for height math.
    const int BrickHeightLdu = 24;  // 1 brick tall  = 24 LDU
    const int PlateHeightLdu = 8;   // 1 plate tall  =  8 LDU (1/3 of a brick)

    readonly HttpClient _http = new();

    // Cache is the single source of truth. Each part number is fetched once per run.
    // StringComparer.OrdinalIgnoreCase: "3003" and "3003" from different .dat casings match.
    readonly Dictionary<string, PartProperties> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Read-only view of the cache for the rest of the program.
    public IReadOnlyDictionary<string, PartProperties> Parts => _cache;

    // Fetch properties for each part number. _cache is the only dedup; repeated numbers skip the API.
    public async Task LoadPartsAsync(IEnumerable<string> partNumbers)
    {
        foreach (var partNo in partNumbers)
        {
            if (_cache.ContainsKey(partNo)) continue;
            var (props, resolved) = await FetchPartPropertiesAsync(partNo);
            _cache[partNo] = props;
            _cache.TryAdd(resolved, props);
        }
    }

    async Task<(PartProperties Props, string ResolvedNo)> FetchPartPropertiesAsync(string partNo)
    {
        foreach (var candidate in LookupCandidates(partNo))
        {
            var props = await TryFetchCatalogItemAsync(candidate, partNo);
            if (props is { } p)
                return (p, candidate);
        }
        throw new InvalidOperationException($"BrickLink has no catalog entry for {partNo}.");
    }

    // LDraw uses suffix letters for mold variants (3709b). BrickLink often lists those as alternates of the base number (3709).
    static IEnumerable<string> LookupCandidates(string partNo)
    {
        yield return partNo;
        var m = Regex.Match(partNo, @"^(\d+)[a-zA-Z]$");
        if (m.Success)
            yield return m.Groups[1].Value;
    }

    async Task<PartProperties?> TryFetchCatalogItemAsync(string catalogNo, string displayNo)
    {
        // "3003.dat" → part number "3003" for the API URL.
        var url = $"https://api.bricklink.com/api/store/v1/items/PART/{Uri.EscapeDataString(catalogNo)}";

        // "using var" disposes (cleans up) the request when this method finishes.
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", Sign("GET", url, creds));

        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        // JsonDocument.Parse reads JSON text; GetProperty navigates to nested fields.
        using var doc = JsonDocument.Parse(body);
        var meta = doc.RootElement.GetProperty("meta");
        if (meta.GetProperty("code").GetInt32() != 200)
            return null;

        var data = doc.RootElement.GetProperty("data");
        double weight = double.Parse(data.GetProperty("weight").GetString()!, CultureInfo.InvariantCulture);

        // BrickLink dim_x/y/z = "Stud Dim." on the website: width × length × height.
        // dim_x = studs wide, dim_y = studs long, dim_z = bricks tall
        // dim_z is omitted "For parts where height is less than one Brick (for example Plates or Tiles), enter dimensions of width and length and omit height by entering the number '0' in the box for height."

        var dims = new[] { "dim_x", "dim_y", "dim_z" }
            .Select(k => double.Parse(data.GetProperty(k).GetString()!, CultureInfo.InvariantCulture))
            .ToArray();

        // Convert the BrickLink studs measurement to LDraw units:
        // LDraw puts the origin at the TOP of the studs (+Y points downward), so the
        // part's center of mass sits halfway down its height: (0, heightLdu/2, 0).
        float heightLdu = HeightFromBrickLinkDims(dims[0], dims[1], dims[2]);

        Console.WriteLine($"      {displayNo,-6}  {weight,5:F2} g   {FormatBrickLinkDims(dims[0], dims[1], dims[2])}");
        // Built here, stored once in _cache by LoadPartsAsync, not returned and cached separately.
        return new PartProperties(weight, new Vector3(0, heightLdu / 2f, 0));
    }

    // Build the OAuth 1.0 Authorization header BrickLink requires.
    static string Sign(string method, string url, BrickLinkCredentials c)
    {
        var oauth = new SortedDictionary<string, string>
        {
            ["oauth_consumer_key"] = c.ConsumerKey!,
            ["oauth_token"] = c.Token!,
            ["oauth_nonce"] = Guid.NewGuid().ToString("N"),       // random unique ID per request
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["oauth_version"] = "1.0"
        };

        var baseString = string.Join("&", new[]
        {
            OAuthEncode(method),
            OAuthEncode(url),
            OAuthEncode(string.Join("&", oauth
                .Select(kv => $"{OAuthEncode(kv.Key)}={OAuthEncode(kv.Value)}")))
        });

        var key = $"{OAuthEncode(c.ConsumerSecret!)}&{OAuthEncode(c.TokenSecret!)}";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
        oauth["oauth_signature"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString)));

        return string.Join(", ", oauth
            .Select(kv => $"{OAuthEncode(kv.Key)}=\"{OAuthEncode(kv.Value)}\""));
    }

    static string OAuthEncode(string v) => Uri.EscapeDataString(v);

    // Load API keys from the project's .env file (default for local testing).
    public static BrickLinkCredentials LoadCredentials()
    {
        string? envPath = FindEnvFile()
            ?? throw new InvalidOperationException(
                "No .env file in the project root. Add your BrickLink credentials to the .env file.");

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LoadDotEnv(envPath, vars);

        var creds = new BrickLinkCredentials(
            vars.GetValueOrDefault("BRICKLINK_CONSUMER_KEY"),
            vars.GetValueOrDefault("BRICKLINK_CONSUMER_SECRET"),
            vars.GetValueOrDefault("BRICKLINK_TOKEN") ?? vars.GetValueOrDefault("BRICKLINK_TOKEN_VALUE"),
            vars.GetValueOrDefault("BRICKLINK_TOKEN_SECRET"));

        if (creds.IsComplete) return creds;

        throw new InvalidOperationException(
            $".env found at {envPath} but credentials are incomplete. " +
            "Required: BRICKLINK_CONSUMER_KEY, BRICKLINK_CONSUMER_SECRET, " +
            "BRICKLINK_TOKEN (or BRICKLINK_TOKEN_VALUE), BRICKLINK_TOKEN_SECRET.");
    }

    // .env is always in the project root. Exe lives in dist/; dotnet run uses cwd.
    static string? FindEnvFile()
    {
        var root = ProjectRoot();
        var path = Path.Combine(root, ".env");
        return File.Exists(path) ? path : null;
    }

    static string ProjectRoot()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (exeDir is not null && Path.GetFileName(exeDir).Equals("dist", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(exeDir)!.FullName;
        return Directory.GetCurrentDirectory();
    }

    // Simple .env parser: KEY=value lines, # comments ignored.
    static void LoadDotEnv(string path, Dictionary<string, string> into)
    {
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            into[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
            // line[..eq]  = substring from start to '='  (C# range syntax)
            // line[(eq+1)..] = substring from after '=' to end
        }
    }

    // BrickLink returns stud dimensions as width × length × height (in bricks).
    // Plates and tiles omit height by setting dim_z to 0. See HeightFromBrickLinkDims.
    // Convert the BrickLink studs measurement to LDraw units
    static float HeightFromBrickLinkDims(double widthStuds, double lengthStuds, double heightBricks)
    {
        // No footprint at all (e.g. technic ball). Treat as a point at the part origin.
        if (widthStuds <= 0 && lengthStuds <= 0 && heightBricks <= 0)
            return 0;

        // Plate 1×2 is entered as 1 × 2 × 0. Height omitted means standard plate thickness.
        if (heightBricks <= 0)
            return PlateHeightLdu;

        // Brick 2×2 is entered as 2 × 2 × 1. Height is in brick units (1 brick = 24 LDU).
        // Fractional values work too (e.g. 0.33 brick ≈ one plate).
        return (float)(heightBricks * BrickHeightLdu);
    }

    static string FormatBrickLinkDims(double widthStuds, double lengthStuds, double heightBricks)
    {
        if (heightBricks <= 0 && (widthStuds > 0 || lengthStuds > 0))
            return $"{widthStuds:G}×{lengthStuds:G}×0 studs (plate, {PlateHeightLdu} LDU tall)";
        return $"{widthStuds:G}×{lengthStuds:G}×{heightBricks:G} studs/bricks";
    }
}

// =========================================================================
// Data types
// =========================================================================
// "record struct" = lightweight value type that holds data (like a tiny class).
// Records get automatic equality, ToString(), etc.

// Physical properties looked up from BrickLink for one part type (weight, local center of mass).
readonly record struct PartProperties(double WeightGrams, Vector3 LocalCenterOfMass);

// API keys. "string?" = nullable string (may be null if not configured).
sealed record BrickLinkCredentials(
    string? ConsumerKey, string? ConsumerSecret, string? Token, string? TokenSecret)
{
    // Expression-bodied property: true when all four fields are non-empty.
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ConsumerKey) && !string.IsNullOrWhiteSpace(ConsumerSecret)
        && !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(TokenSecret);
}
