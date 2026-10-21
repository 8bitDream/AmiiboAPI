using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Serialization;
using AmiiboGameList.ConsoleClasses;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace AmiiboGameList;

public class Program
{
    private const string Switch2TitleListUrl = "https://switchbrew.org/wiki/Switch_2:_Title_list/Games";

    /// <summary>
    /// A shared HttpClient instance to be used throughout the program.
    /// </summary>
    public static HttpClient client = new();
    private static readonly SemaphoreSlim amiilifeRequestLock = new(1, 1);
    private static DateTimeOffset nextAmiilifeRequestAt = DateTimeOffset.MinValue;

    /// <summary>
    /// The lazy instance of the AmiiboDataBase
    /// </summary>
    private static readonly Lazy<DBRootobjectInstance> lazy = new(() => new DBRootobjectInstance());

    /// <summary>
    /// Gets the instance of the AmiiboDataBase.
    /// </summary>
    /// <value>
    /// The instance of the AmiiboDataBase.
    /// </value>
    public static DBRootobjectInstance BRootobject => lazy.Value;
    private static string inputPath;
    private static string outputPath = @"games_info.json";
    private static int parallelism = 4;
    private static int amiilifeDelayMs = 1500;
    private static int amiilifeAttempts = 8;
    private static bool allowMissingGames;
    private static readonly ConcurrentDictionary<Hex, Games> export = new();

    static Program()
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AGLG/1.4 (+https://github.com/MrKev312/AmiiboGameListGenerator)");
        client.Timeout = TimeSpan.FromSeconds(60);
    }

    private static async Task WaitForAmiilifeSlotAsync()
    {
        await amiilifeRequestLock.WaitAsync();
        try
        {
            TimeSpan delay = nextAmiilifeRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            nextAmiilifeRequestAt = DateTimeOffset.UtcNow.AddMilliseconds(amiilifeDelayMs);
        }
        finally
        {
            amiilifeRequestLock.Release();
        }
    }

    private static async Task<string> GetStringWithStatusAsync(string url)
    {
        await WaitForAmiilifeSlotAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = await client.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync();
        }

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
        HttpRequestException exception = new(
            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).",
            null,
            response.StatusCode);
        exception.Data["RequestUrl"] = url;
        exception.Data["RetryAfter"] = retryAfter;
        throw exception;
    }

    public static async Task<string> GetAmiilifeStringAsync(string url, int? attempts = null)
    {
        attempts ??= amiilifeAttempts;

        var handleError = new Func<int, string, HttpRequestException, Task<bool>>(async (attempt, message, httpRequestException) =>
        {
            Debugger.Log(message, Debugger.DebugLevel.Error);

            if (attempt >= (attempts.Value - 1))
            {
                return true;
            }

            TimeSpan retryAfter = httpRequestException?.Data["RetryAfter"] as TimeSpan? ?? TimeSpan.Zero;
            TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) * 5);
            TimeSpan delay = retryAfter > backoff ? retryAfter : backoff;
            delay += TimeSpan.FromMilliseconds(Random.Shared.Next(250, 1500));

            Debugger.Log($"Retrying in {delay.TotalSeconds:N0} seconds", Debugger.DebugLevel.Verbose);
            await Task.Delay(delay);

            return false;
        });

        for (int i = 0; i < attempts.Value; i++)
        {
            try
            {
                return await GetStringWithStatusAsync(url);
            }
            catch (WebException ex)
            {
                if (await handleError(i, $"({i + 1}/{attempts}) Error while loading {url}\n{ex.Message}", null))
                {
                    throw;
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                if (await handleError(i, $"({i + 1}/{attempts}) Timeout error while loading {url}\n{ex.Message}", null))
                {
                    throw;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode.HasValue && ((int)ex.StatusCode > 499 && (int)ex.StatusCode < 600 || (int)ex.StatusCode == 429))
            {
                if (await handleError(i, $"({i + 1}/{attempts}) HTTP {(int)ex.StatusCode} error while loading {url}\n{ex.Message}", ex))
                {
                    throw;
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        throw new Exception("Error occurred in Program.GetAmiilifeStringAsync.  This should never be reached.");
    }

    private static bool IsValidTitleId(string titleId)
    {
        return titleId is not null
            && titleId.Length >= 16
            && Regex.IsMatch(titleId[..16], "^[0-9A-Fa-f]{16}$", RegexOptions.CultureInvariant);
    }

    private static List<Switch2Game> ParseSwitchbrewGames(string switchbrewPage)
    {
        HtmlDocument htmlDocument = new();
        htmlDocument.LoadHtml(switchbrewPage);

        List<Switch2Game> games = new();
        foreach (HtmlNode table in htmlDocument.DocumentNode.SelectNodes("//table") ?? Enumerable.Empty<HtmlNode>())
        {
            HtmlNode headerRow = table.SelectSingleNode(".//tr[th]");
            HtmlNodeCollection headerCells = headerRow?.SelectNodes("./th");
            if (headerCells is null)
            {
                continue;
            }

            int titleIdColumn = FindColumn(headerCells, "ApplicationId");
            int nameColumn = FindColumn(headerCells, "Description");
            int typeColumn = FindColumn(headerCells, "Type");
            if (titleIdColumn < 0 || nameColumn < 0)
            {
                continue;
            }

            int lastRequiredColumn = Math.Max(titleIdColumn, nameColumn);
            foreach (HtmlNode row in table.SelectNodes(".//tr[td]") ?? Enumerable.Empty<HtmlNode>())
            {
                HtmlNodeCollection cells = row.SelectNodes("./td");
                if (cells is null || cells.Count <= lastRequiredColumn)
                {
                    continue;
                }

                string titleId = CleanTableCell(cells[titleIdColumn].InnerText);
                string name = CleanTableCell(cells[nameColumn].InnerText);
                if (!IsValidTitleId(titleId) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (typeColumn >= 0
                    && (cells.Count <= typeColumn
                        || !CleanTableCell(cells[typeColumn].InnerText).Equals("Game", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                games.Add(new Switch2Game
                {
                    titleid = titleId[..16],
                    name = name
                });
            }
        }

        return games
            .GroupBy(game => $"{game.titleid}:{game.name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static int FindColumn(HtmlNodeCollection headerCells, string columnName)
    {
        for (int index = 0; index < headerCells.Count; index++)
        {
            if (CleanTableCell(headerCells[index].InnerText).Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string CleanTableCell(string value)
    {
        return Regex.Replace(WebUtility.HtmlDecode(value ?? string.Empty), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Mains this instance.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="XmlSerializer">typeof(Switchreleases)</exception>
    public static int Main(string[] args)
    {
        ParseArguments(args);

        // Load Regex for removing copyrights, trademarks, etc.
        Regex rx = new(@"[®™]", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Load amiibo data
        Debugger.Log("Loading amiibo");
        try
        {
            string amiiboJSON = default;
            if (string.IsNullOrEmpty(inputPath))
            {
                Debugger.Log("Downloading amiibo database", Debugger.DebugLevel.Verbose);
                try
                {
                    amiiboJSON = Program.client.GetStringAsync("https://raw.githubusercontent.com/8bitDream/AmiiboAPI/dev/database/amiibo.json").Result;
                }
                catch (Exception e)
                {
                    Debugger.Log("Error while downloading amiibo.json, please check internet:\n" + e.Message, Debugger.DebugLevel.Error);
                    Environment.Exit((int)Debugger.ReturnType.InternetError);
                }
            }
            else
            {
                amiiboJSON = File.ReadAllText(inputPath);
            }

            Debugger.Log("Processing amiibo database", Debugger.DebugLevel.Verbose);
            BRootobject.rootobject = JsonConvert.DeserializeObject<DBRootobject>(amiiboJSON);

            foreach (KeyValuePair<Hex, DBAmiibo> entry in BRootobject.rootobject.amiibos)
            {
                entry.Value.ID = entry.Key;
            }
        }
        catch (Exception ex)
        {
            Debugger.Log("Error loading amiibo.json:\n" + ex.Message, Debugger.DebugLevel.Error);
            Environment.Exit((int)Debugger.ReturnType.DatabaseLoadingError);
        }

        // Load Wii U games
        Debugger.Log("Loading Wii U games");
        Debugger.Log("Processing Wii U database", Debugger.DebugLevel.Verbose);
        try
        {
            Games.WiiUGames = JsonConvert.DeserializeObject<List<GameInfo>>(Properties.Resources.WiiU);
        }
        catch (Exception ex)
        {
            Debugger.Log("Error loading Wii U games:\n" + ex.Message, Debugger.DebugLevel.Error);
            Environment.Exit((int)Debugger.ReturnType.DatabaseLoadingError);
        }

        // Load 3DS games
        Debugger.Log("Loading 3DS games");
        try
        {
            byte[] DSDatabase = default;
            try
            {
                Debugger.Log("Downloading 3DS database", Debugger.DebugLevel.Verbose);
                DSDatabase = Program.client.GetByteArrayAsync("http://3dsdb.com/xml.php").Result;
            }
            catch (Exception ex)
            {
                Debugger.Log("Error while downloading 3DS database, please check internet:\n" + ex.Message, Debugger.DebugLevel.Error);
                Environment.Exit((int)Debugger.ReturnType.InternetError);
            }

            Debugger.Log("Processing 3DS database", Debugger.DebugLevel.Verbose);
            XmlSerializer serializer = new(typeof(DSreleases));
            using MemoryStream stream = new(DSDatabase);
            Games.DSGames = ((DSreleases)serializer.Deserialize(stream)).release.ToList();
        }
        catch (Exception ex)
        {
            Debugger.Log("Error loading 3DS games:\n" + ex.Message, Debugger.DebugLevel.Error);
            Environment.Exit((int)Debugger.ReturnType.DatabaseLoadingError);
        }

        // Load Switch games
        Debugger.Log("Loading Switch games");
        try
        {
            string BlawarDatabase = default;
            // Try loading the database
            Debugger.Log("Downloading Switch database", Debugger.DebugLevel.Verbose);
            try
            {
                BlawarDatabase = Program.client.GetStringAsync("https://raw.githubusercontent.com/blawar/titledb/master/US.en.json").Result;
            }
            catch (Exception ex)
            {
                Debugger.Log("Error while downloading switch database, please check internet:\n" + ex.Message, Debugger.DebugLevel.Error);
                Environment.Exit((int)Debugger.ReturnType.InternetError);
            }

            Debugger.Log("Processing Switch database", Debugger.DebugLevel.Verbose);
            // Parse the loaded JSON
            Games.SwitchGames = (Lookup<string, string>)JsonConvert.DeserializeObject<Dictionary<Hex, SwitchGame>>(BlawarDatabase)
                // Make KeyValuePairs to turn into a Lookup and decode the HTML encoded name
                .Select(x => new KeyValuePair<string, string>(HttpUtility.HtmlDecode(x.Value.name), x.Value.id)).Where(y => y.Value != null)
                // Convert to Lookup for faster searching while allowing multiple values per key and apply regex
                .ToLookup(x => rx.Replace(x.Key, "").Replace('’', '\'').ToLower(), x => x.Value);
        }
        catch (Exception ex)
        {
            Debugger.Log("Error loading Switch games:\n" + ex.Message, Debugger.DebugLevel.Error);
            Environment.Exit((int)Debugger.ReturnType.DatabaseLoadingError);
        }

        // Load Switch 2 games
        Debugger.Log("Loading Switch 2 games");
        Games.Switch2Games = new();
        List<Switch2Game> ns2Games = new();
        HashSet<string> ns2TitleIds = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            byte[] NS2Database = default;
            try
            {
                Debugger.Log("Downloading NS2 database", Debugger.DebugLevel.Verbose);
                NS2Database = Program.client.GetByteArrayAsync("http://ns2db.com/xml.php").Result;
            }
            catch (Exception ex)
            {
                Debugger.Log("Error while downloading NS2 database, please check internet:\n" + ex.Message, Debugger.DebugLevel.Warn);
            }

            if (NS2Database?.Length > 0)
            {
                Debugger.Log("Processing NS2 database", Debugger.DebugLevel.Verbose);
                XmlSerializer serializer = new(typeof(Switch2Releases));
                using MemoryStream stream = new(NS2Database);
                ns2Games = ((Switch2Releases)serializer.Deserialize(stream)).release?.ToList() ?? new();
                ns2TitleIds = ns2Games
                    .Where(game => IsValidTitleId(game.titleid))
                    .Select(game => game.titleid[..16])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            Debugger.Log("Error loading NS2 database:\n" + ex.Message, Debugger.DebugLevel.Warn);
        }

        // Load Switch 2 games from Switchbrew and merge with NS2, favoring NS2 records
        try
        {
            Debugger.Log("Downloading Switch 2 database", Debugger.DebugLevel.Verbose);
            string switchbrewPage = Program.client.GetStringAsync(Switch2TitleListUrl).Result;

            Debugger.Log("Processing Switch 2 database", Debugger.DebugLevel.Verbose);
            List<Switch2Game> switchbrewGames = ParseSwitchbrewGames(switchbrewPage);
            List<Switch2Game> validNs2Games = ns2Games
                .Where(game => IsValidTitleId(game.titleid))
                .Select(game => new Switch2Game
                {
                    titleid = game.titleid[..16],
                    name = game.name
                })
                .ToList();
            Games.Switch2Games = validNs2Games
                .Concat(switchbrewGames
                    .Where(game => !ns2TitleIds.Contains(game.titleid)))
                .ToList();
        }
        catch (Exception ex)
        {
            Debugger.Log("Error loading Switch 2 games:\n" + ex.Message, Debugger.DebugLevel.Error);
            Environment.Exit((int)Debugger.ReturnType.DatabaseLoadingError);
        }

        Debugger.Log("Done loading!");

        // List to keep track of missing games
        Games.missingGames = new();

        // Counter to keep track of how many amiibo we've done
        int AmiiboCounter = 0;
        int TotalAmiibo = BRootobject.rootobject.amiibos.Count;
        List<string> amiiboFailures = new();

        Debugger.Log("Processing amiibo");
        // Iterate over all amiibo and get game info
        _ = Parallel.ForEach(BRootobject.rootobject.amiibos, new ParallelOptions()
        {
            MaxDegreeOfParallelism = parallelism
        }, (DBamiibo) =>
        {
            Games exportAmiibo = default;
            try
            {
                exportAmiibo = ParseAmiibo(DBamiibo.Value);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                string failedUrl = ex.Data["RequestUrl"] as string ?? "(request URL unavailable)";
                string failure = $"{DBamiibo.Value.OriginalName} ({DBamiibo.Value.Name}) — HTTP 404 — {failedUrl}";
                Debugger.Log($"Failed to load game info for {failure}\n{ex.Message}", Debugger.DebugLevel.Error);
                lock (amiiboFailures)
                {
                    amiiboFailures.Add(failure);
                }

                // Keep this amiibo in the output and continue processing the rest.
                exportAmiibo = new Games();
            }
            catch (WebException ex)
            {
                Debugger.Log($"Internet error when processing {DBamiibo.Value.Name} ({DBamiibo.Value.OriginalName})\n{ex.Message}\n{DBamiibo.Value.URL}", Debugger.DebugLevel.Error);
                Environment.Exit((int)Debugger.ReturnType.InternetError);
            }
            catch (Exception ex)
            {
                Debugger.Log($"Unexpected error when processing {DBamiibo.Value.Name} ({DBamiibo.Value.OriginalName})\n{ex.Message}", Debugger.DebugLevel.Error);
                Environment.Exit((int)Debugger.ReturnType.UnknownError);
            }

            if (!export.TryAdd(DBamiibo.Key, exportAmiibo))
            {
                throw new InvalidOperationException($"Duplicate amiibo key: {DBamiibo.Key}");
            }

            // Show which amiibo just got added
            int completedAmiiboCount = Interlocked.Increment(ref AmiiboCounter);
            Debugger.Log($"{completedAmiiboCount:D3}/{TotalAmiibo} Done with {DBamiibo.Value.OriginalName} ({DBamiibo.Value.amiiboSeries})", Debugger.DebugLevel.Verbose);
        });

        // Sort export object
        var SortedAmiibos = new AmiiboKeyValue
        {
            amiibos = export.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };

        // Write the SortedAmiibos to file as an tab-indented json
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(SortedAmiibos, Formatting.Indented).Replace("  ", "\t"));

        // Inform we're done
        Debugger.Log("\nDone generating the JSON!");

        if (amiiboFailures.Count != 0)
        {
            Debugger.Log($"{amiiboFailures.Count} amiibo failed to load game info (HTTP 404):", Debugger.DebugLevel.Error);
            foreach (string failure in amiiboFailures)
            {
                Debugger.Log("\t" + failure, Debugger.DebugLevel.Error);
            }
        }

        // Show missing games
        if (Games.missingGames.Count != 0)
        {
            Debugger.Log("However, the following games couldn't find their titleids and thus couldn't be added:", Debugger.DebugLevel.Warn);
            foreach (string Game in Games.missingGames.Distinct())
            {
                Debugger.Log("\t" + Game, Debugger.DebugLevel.Warn);
            }

            if (allowMissingGames)
            {
                return 0;
            }

            return (int)Debugger.ReturnType.SuccessWithErrors;
        }
        else
        {
            return 0;
        }
    }

    private static Games ParseAmiibo(DBAmiibo DBamiibo)
    {
        Games ExAmiibo = new();

        HtmlDocument htmlDoc = new();

        htmlDoc.LoadHtml(
            WebUtility.HtmlDecode(
                Program.GetAmiilifeStringAsync(DBamiibo.URL).GetAwaiter().GetResult()
            )
        );

        // Get the games panel. SelectNodes returns null when the page has no matches.
        // This is valid for amiibo with no game entries (and can also happen when
        // amiibo.life changes the page markup), so treat it as an empty collection.
        HtmlNodeCollection GamesPanel = htmlDoc.DocumentNode.SelectNodes(
            "//*[contains(concat(' ', normalize-space(@class), ' '), ' games ') and " +
            "contains(concat(' ', normalize-space(@class), ' '), ' panel ')]/a");
        if (GamesPanel is null || GamesPanel.Count == 0)
        {
            Debugger.Log("No games found for " + DBamiibo.Name, Debugger.DebugLevel.Verbose);
            return ExAmiibo;
        }

        // Iterate over each game in the games panel
        foreach (HtmlNode node in GamesPanel)
        {
            // Get the name of the game
            HtmlNode gameNameNode = node.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' name ')]//text()[normalize-space()]");
            // amiibo.life renders the platform in a sibling .system span, not inside .name.
            HtmlNode platformNode = node.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' system ')]");
            if (gameNameNode is null || platformNode is null)
            {
                Debugger.Log("Skipping malformed game entry for " + DBamiibo.Name, Debugger.DebugLevel.Warn);
                continue;
            }

            Game game = new()
            {
                gameName = gameNameNode.InnerText.Trim().Replace("Poochy & ", "").Trim().Replace("Ace Combat Assault Horizon Legacy +", "Ace Combat Assault Horizon Legacy+").Replace("Power Pros", "Jikkyou Powerful Pro Baseball"),
                gameID = new(),
                amiiboUsage = new()
            };

            // Get the amiibo usages
            foreach (HtmlNode amiiboUsage in node.SelectNodes(".//*[contains(concat(' ', normalize-space(@class), ' '), ' features ')]/li") ?? Enumerable.Empty<HtmlNode>())
            {
                game.amiiboUsage.Add(new()
                {
                    Usage = Regex.Replace(amiiboUsage.GetDirectInnerText().Trim(), @"\s+", " "),
                    write = amiiboUsage.SelectSingleNode("em")?.InnerText == "(Read+Write)"
                });
            }

            // Sort amiiboUsage alphabetically by Usage
            game.amiiboUsage.Sort((x, y) => string.Compare(x.Usage, y.Usage, StringComparison.OrdinalIgnoreCase));

            if (DBamiibo.Name == "Shadow Mewtwo")
            {
                game.gameName = "Pokkén Tournament";
            }

            // Add game to the correct console and get correct titleid
            Regex rgx = new("[^a-zA-Z0-9 -]");
            switch (platformNode.InnerText.Trim().ToLower())
            {
                case "switch 2":
                try
                {
                    List<Switch2Game> games = Games.Switch2Games.FindAll(Switch2Games =>
                        IsValidTitleId(Switch2Games.titleid)
                        && !string.IsNullOrWhiteSpace(Switch2Games.name)
                        && rgx.Replace(WebUtility.HtmlDecode(Switch2Games.name).ToLower(), "").Contains(rgx.Replace(game.gameName.ToLower(), "")));

                    game.gameID = Games.SwitchGames[game.sanatizedGameName.ToLower()].ToList();
                    games.ForEach(Switch2Games => game.gameID.Add(Switch2Games.titleid[..16]));

                    if (game.gameID.Count == 0)
                    {
                        throw new Exception();
                    }

                    game.gameID = game.gameID.Order().Distinct().ToList();
                    lock (ExAmiibo.gamesSwitch2)
                    {
                        ExAmiibo.gamesSwitch2.Add(game);
                    }
                }
                catch
                {
                    lock (Games.missingGames)
                    {
                        Games.missingGames.Add(game.gameName + " (Switch 2)");
                    }
                }
                    break;
                case "switch":
                    try
                    {
                        game.gameID = Games.SwitchGames[game.sanatizedGameName.ToLower()].ToList();

                        if (game.gameID.Count == 0)
                        {
                            game.gameID = game.sanatizedGameName switch
                            {
                                "Cyber Shadow" => new() { "0100C1F0141AA000" },
                                "Jikkyou Powerful Pro Baseball" => new() { "0100E9C00BF28000" },
                                "Shovel Knight Pocket Dungeon" => new() { "01006B00126EC000" },
                                "Shovel Knight Showdown" => new() { "0100B380022AE000" },
                                "Super Kirby Clash" => new() { "01003FB00C5A8000" },
                                "The Legend of Zelda: Echoes of Wisdom" => new() { "01008CF01BAAC000" },
                                "The Legend of Zelda: Skyward Sword HD" => new() { "01002DA013484000" },
                                "Yu-Gi-Oh! Rush Duel Saikyo Battle Royale" => new() { "01003C101454A000" },
                                _ => throw new Exception()
                            };
                        }

                        game.gameID = game.gameID.Order().Distinct().ToList();
                        lock (ExAmiibo.gamesSwitch)
                        {
                            ExAmiibo.gamesSwitch.Add(game);
                        }
                    }
                    catch
                    {
                        lock (Games.missingGames)
                        {
                            Games.missingGames.Add(game.gameName + " (Switch)");
                        }
                    }

                    break;
                case "wii u":
                    try
                    {
                        string[] gameIDs = Games.WiiUGames.Find(WiiUGame => WiiUGame.Name.Contains(game.gameName, StringComparer.OrdinalIgnoreCase))?.Ids;
                        if (gameIDs?.Length == 0 || gameIDs == null)
                        {
                            game.gameID = game.gameName switch
                            {
                                "Shovel Knight Showdown" => new() { "000500001016E100", "0005000010178F00", "0005000E1016E100", "0005000E10178F00", "0005000E101D9300" },
                                _ => throw new Exception()
                            };
                        }
                        else
                        {
                            foreach (string ID in gameIDs)
                            {
                                game.gameID.Add(ID[..16]);
                            }
                        }

                        game.gameID = game.gameID.Order().Distinct().ToList();

                        lock (ExAmiibo.gamesWiiU)
                        {
                            ExAmiibo.gamesWiiU.Add(game);
                        }
                    }
                    catch
                    {
                        lock (Games.missingGames)
                        {
                            Games.missingGames.Add(game.gameName + " (Wii U)");
                        }
                    }

                    break;
                case "3ds":
                    try
                    {
                        List<DSreleasesRelease> games = Games.DSGames.FindAll(DSGame => rgx.Replace(WebUtility.HtmlDecode(DSGame.name).ToLower(), "").Contains(rgx.Replace(game.gameName.ToLower(), "")));
                        if (games.Count == 0)
                        {
                            game.gameID = game.gameName switch
                            {
                                "Style Savvy: Styling Star" => new() { "00040000001C2500" },
                                "Metroid Prime: Blast Ball" => new() { "0004000000175300" },
                                "Mini Mario & Friends amiibo Challenge" => new() { "000400000016C300", "000400000016C200" },
                                "Team Kirby Clash Deluxe" => new() { "00040000001AB900", "00040000001AB800" },
                                "Kirby's Extra Epic Yarn" => new() { "00040000001D1F00" },
                                "Kirby's Blowout Blast" => new() { "0004000000196F00" },
                                "BYE-BYE BOXBOY!" => new() { "00040000001B5400", "00040000001B5300" },
                                "Azure Striker Gunvolt 2" => new() { "00040000001A6E00" },
                                "niconico app" => new() { "0005000010116400" },
                                _ => throw new Exception(),
                            };
                        }

                        games.ForEach(DSGame =>
                            game.gameID.Add(DSGame.titleid[..16]));

                        game.gameID = game.gameID.Order().Distinct().ToList();

                        lock (ExAmiibo.games3DS)
                        {
                            ExAmiibo.games3DS.Add(game);
                        }
                    }
                    catch
                    {
                        lock (Games.missingGames)
                        {
                            Games.missingGames.Add(game.gameName + " (3DS)");
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        // Sort all gamelists
        ExAmiibo.gamesSwitch2.Sort((x, y) => string.Compare(x.gameName, y.gameName, StringComparison.OrdinalIgnoreCase));
        ExAmiibo.gamesSwitch.Sort((x, y) => string.Compare(x.gameName, y.gameName, StringComparison.OrdinalIgnoreCase));
        ExAmiibo.gamesWiiU.Sort((x, y) => string.Compare(x.gameName, y.gameName, StringComparison.OrdinalIgnoreCase));
        ExAmiibo.games3DS.Sort((x, y) => string.Compare(x.gameName, y.gameName, StringComparison.OrdinalIgnoreCase));

        // Return the created amiibo
        return ExAmiibo;
    }

    private static void ParseArguments(string[] args)
    {
        if (args.Length != 0)
        {
            Debugger.Log($"Running with these arguments: {string.Join(' ', args)}");

            // Show help message
            if (args.Contains("-h") || args.Contains("-help"))
            {
                StringBuilder sB = new();
                _ = sB.AppendLine("Usage:");
                _ = sB.AppendLine("-i | -input {filepath} to specify input json location");
                _ = sB.AppendLine("-o | -output {filepath} to specify output json location");
                _ = sB.AppendLine("-p | -parallelism {value} to specify the max degree of parallelism");
                _ = sB.AppendLine("-amiilife-delay-ms {value} to set the minimum delay between amiibo.life requests, defaults to 1500");
                _ = sB.AppendLine("-amiilife-attempts {value} to set the number of amiibo.life retry attempts, defaults to 8");
                _ = sB.AppendLine("-l | -log {value} to set the logging level, can pick from verbose, info, warn, error or from 0 to 3 respectively");
                _ = sB.AppendLine("-allow-missing to return 0 even if some games are missing titleids");
                _ = sB.AppendLine("-h | -help to show this message");
                Debugger.Log(sB.ToString());
                Environment.Exit(0);
            }
        }

        Debugger.CurrentDebugLevel = Debugger.DebugLevel.Info;

        // Loop through arguments
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-i":
                case "-input":
                    if (File.Exists(args[i + 1]) || args.Contains("-i"))
                    {
                        inputPath = args[i + 1];
                        i++;
                        continue;
                    }
                    else
                    {
                        throw new FileNotFoundException($"Input file '{args[i + 1]}' not found");
                    }
                case "-o":
                case "-output":
                    if (Directory.Exists(Path.GetDirectoryName(args[i + 1])))
                    {
                        outputPath = args[i + 1];
                        i++;
                        continue;
                    }
                    else
                    {
                        throw new DirectoryNotFoundException($"Input directory '{args[i + 1]}' not found");
                    }
                case "-p":
                case "-parallelism":
                    parallelism = int.Parse(args[i + 1]);
                    i++;
                    continue;

                case "-amiilife-delay-ms":
                    amiilifeDelayMs = int.Parse(args[i + 1]);
                    i++;
                    continue;

                case "-amiilife-attempts":
                    amiilifeAttempts = int.Parse(args[i + 1]);
                    i++;
                    continue;

                case "-l":
                case "-log":
                    if (Enum.TryParse(args[i + 1], true, out Debugger.DebugLevel debugLevel) && Enum.IsDefined(typeof(Debugger.DebugLevel), debugLevel))
                    {
                        Debugger.CurrentDebugLevel = debugLevel;
                        Debugger.Log($"Setting DebugLevel to {Enum.GetName(typeof(Debugger.DebugLevel), debugLevel)}", Debugger.DebugLevel.Verbose);
                        i++;
                        continue;
                    }
                    else
                    {
                        throw new ArgumentException($"Incorrect debug level passed: {args[i + 1]}");
                    }
                case "-allow-missing":
                    allowMissingGames = true;
                    continue;
                default:
                    break;
            }
        }

        Debugger.Log("Done parsing arguments", Debugger.DebugLevel.Verbose);
        Debugger.Log(default);
    }
}
