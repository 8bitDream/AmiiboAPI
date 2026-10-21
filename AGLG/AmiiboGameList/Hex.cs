using System.ComponentModel;
using System.Globalization;
using Newtonsoft.Json;

namespace AmiiboGameList;

[JsonConverter(typeof(ToStringJsonConverter))]
[TypeConverter(typeof(HexClassConverter))]
public partial class Hex
{
    public ulong HexValue;

    public Hex(ulong HexValue) => this.HexValue = HexValue;

    // Converters
    public static explicit operator Hex(string value) => new(Convert.ToUInt64(value, 16));
    public override string ToString() => "0x" + HexValue.ToString("x16");
}

// Comparer for sorting
public partial class Hex : IComparable
{
    public int CompareTo(object obj) => HexValue.CompareTo(((Hex)obj).HexValue);
}

public class HexClassConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext context,
    Type sourceType) => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    public override object ConvertFrom(ITypeDescriptorContext context,
     CultureInfo culture, object value) => value is string @string ? new Hex(Convert.ToUInt64(@string, 16)) : base.ConvertFrom(context, culture, value);
    public override object ConvertTo(ITypeDescriptorContext context,
    CultureInfo culture, object value, Type destinationType) => destinationType == typeof(string) ? ((Hex)value).ToString() : base.ConvertTo(context, culture, value, destinationType);
}

public class ToStringJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => true;

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => writer.WriteValue(value.ToString());

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) => throw new NotImplementedException();
}
