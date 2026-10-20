namespace AmiiboGameList.ConsoleClasses;

/// <summary>Class containing an array of Nintendo Switch 2 games</summary>
[Serializable()]
[System.ComponentModel.DesignerCategory("code")]
[System.Xml.Serialization.XmlRoot("releases")]
public class Switch2Releases
{
	[System.Xml.Serialization.XmlElement("release")]
	public Switch2Game[] release;
}

/// <summary>Class for each Nintendo Switch 2 game.</summary>
[Serializable()]
[System.ComponentModel.DesignerCategory("code")]
[System.Xml.Serialization.XmlType(AnonymousType = true)]
public class Switch2Game
{
	public string name;
	public string titleid;
}
