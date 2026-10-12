using System.Text;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// Cimatron 2026.0 cutters/holders CSV as ExportCuttersAndHoldersToCSV wrote them at Meimad on 2026-10-12 (UTF-16, '|'),
/// shortened to the rows and leading columns the exporter reads; the column-ID rows are Cimatron's own.
/// </summary>
internal static class CimatronToolCsvSamples
{
    private const string CutterIds =
        "1101|1102|1103|1201|1202|1203|2101|2102|2103|2104|2105|2106|2107|2108|2109|2110|2111|2112|2113|2114|2130|2121|2115|2122|2117|2116|2118|2119|2120|2123|2124|2125|2126|2127|2129|2201|2202|2203|2206|2204|2205|2207|2208|2209|2210|2212|2211|2213|3101|3102|3103|3104|3105|4101|4102";

    public static string Cutters(int firstNumber = 29, int secondNumber = 40) => string.Join("\r\n",
        "//|Do Not Delete this row; It contains the Validation Data|Information Type|Cutters No.:|Holders No.:|Units:|mm|inch||Logical Check Box:|TRUE|FALSE",
        "//|Cutters No.:|3|Units:|mm|Holders No.:|2",
        "CimatronE2026.00|1000|3|100|101|7000|2",
        "",
        "Cutter Name|Comment|Website|Magazine No.|Diameter Compensation|Length Compensation|Technology|Tip/Type|Catalog Name|Thread Type|Diameter|Corner Radius|Use Full Cutter Length|Full Cutter Length|Clear Length|Cut Length|Taper|Taper Angle",
        CutterIds,
        $"MERASEK_16|No comment||{firstNumber}|||210101|210201|||16|0|0||52|20|0|||||||||||||||||||0|||||||0||||||HOLDER-50_NEW|||||5200|10000",
        $"MERCUZ_8|No comment||{secondNumber}|||210101|210201|||0.01|0|0||52|4|1|45|||||||||8.01|||||0|4|||0|||||||0||||||BT40 ER32 X 100",
        "CADURI_6|No comment||3|||210101|210202|||6|3|0||29|25|0|||||||||||||||||||0|||||||0||||||NO_SUCH_HOLDER",
        "");

    public static string Holders() => string.Join("\r\n",
        "//|Do Not Delete this row; It contains the Validation Data|Units:|mm|inch",
        "//|Holders No.:|2|Units:|mm",
        "CimatronE2026.00|7000|2|100|101",
        "",
        "Holder Name|Comment|Segment Num.|Spindle Segment Num.||||||Adaptor Type|Display Type||Lower Diameter_1|Upper Diameter_1|Conic Height_1|Total Height_1",
        "7001|7002|7003|7004|7005|7006|7007|7008|7009|7010|7901|7900|7011|7012|7013|7014|7021|7022|7023|7024|7031|7032|7033|7034|7041|7042|7043|7044",
        "HOLDER-50_NEW||1|0|0|0||0|||0|0|50|50|0|65|0|0|0|0|0|0|0|0|0|0|0|0",
        // Three holder stages; a spindle stage after them would not count.
        "BT40 ER32 X 100|HAAS MAZAK MAKINO|3|1|0|0||0|||0|0|38|50|3|69|50|61|5.5|5.5|63|63|0|25|80|80|0|40",
        "");

    public static void Write(string cuttersPath, string holdersPath, int firstNumber = 29, int secondNumber = 40)
    {
        File.WriteAllText(cuttersPath, Cutters(firstNumber, secondNumber), Encoding.Unicode);
        File.WriteAllText(holdersPath, Holders(), Encoding.Unicode);
    }
}
