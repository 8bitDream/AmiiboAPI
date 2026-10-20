using AmiiboGameList.ConsoleClasses;
using Newtonsoft.Json;

namespace AmiiboGameList;

/// <summary>Class to hold all the game data for all consoles.</summary>
public class Games
{
    [JsonIgnore]
    internal static List<GameInfo> WiiUGames;
    [JsonIgnore]
    internal static List<DSreleasesRelease> DSGames;
    [JsonIgnore]
    internal static Lookup<string, string> SwitchGames;
    [JsonIgnore]
    internal static List<Switch2Game> Switch2Games;
    [JsonIgnore]
    internal static List<string> missingGames;

    /// <summary>Initializes a new instance of the <see cref="Games" /> class.</summary>
    public Games()
    {
        games3DS = new();
        gamesWiiU = new();
        gamesSwitch = new();
        gamesSwitch2 = new();
    }

    /// <summary>Gets or sets the games3DS.</summary>
    /// <value>The games3DS.</value>
    public List<Game> games3DS { get; set; }
    /// <summary>Gets or sets the gamesWiiU.</summary>
    /// <value>The gamesWiiU.</value>
    public List<Game> gamesWiiU { get; set; }
    /// <summary>Gets or sets the gamesSwitch.</summary>
    /// <value>The gamesSwitch.</value>
    public List<Game> gamesSwitch { get; set; }
    /// <summary>Gets or sets the gamesSwitch2.</summary>
    /// <value>The gamesSwitch2.</value>
    public List<Game> gamesSwitch2 { get; set; }
}
/// <summary>Class to hold all data for individual game data.</summary>
public class Game : IComparable<Game>
{
    /// <summary>Gets or sets the name of the game.</summary>
    /// <value>The name of the game.</value>
    private string originalGameName;
    [JsonIgnore]
    public string sanatizedGameName;
    public string gameName
    {
        get => originalGameName; set
        {
            originalGameName = value;
            sanatizedGameName = value switch
            {
                "The Legend of Zelda: Skyward Sword HD" => "The Legend of Zelda: Skyward Sword HD",
                "Mario + Rabbids: Kingdom Battle" => "Mario + Rabbids Kingdom Battle",
                "Shovel Knight" => "Shovel Knight: Treasure Trove",
                "Little Nightmares: Complete Edition" => "Little Nightmares Complete Edition",
                _ => value
            };
        }
    }
    /// <summary>Gets or sets the game identifier.</summary>
    /// <value>The game identifier.</value>
    public List<string> gameID { get; set; }
    /// <summary>Gets or sets the amiibo usage.</summary>
    /// <value>The amiibo usage.</value>
    public List<AmiiboUsage> amiiboUsage { get; set; }

    /// <summary>
    /// Compares the current instance with another object of the same type and returns an integer that indicates whether the current instance precedes, follows, or occurs in the same position in the sort order as the other object.
    /// </summary>
    /// <param name="other">An object to compare with this instance.</param>
    /// <returns>
    /// A value that indicates the relative order of the objects being compared. The return value has these meanings:
    /// <list type="table"><listheader><term> Value</term><term> Meaning</term></listheader><item><description> Less than zero</description><description> This instance precedes <paramref name="other" /> in the sort order.</description></item><item><description> Zero</description><description> This instance occurs in the same position in the sort order as <paramref name="other" />.</description></item><item><description> Greater than zero</description><description> This instance follows <paramref name="other" /> in the sort order.</description></item></list></returns>
    public int CompareTo(Game other) => gameName.CompareTo(other.gameName);
}

public class AmiiboUsage
{
    /// <summary>String explaining how the amiibo will be used in a game.</summary>
    public string Usage;
    /// <summary>Bool to signify if this AmiiboUsage will write to the amiibo.</summary>
    public bool write;
}
