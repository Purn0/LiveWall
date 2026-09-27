namespace LiveWall.Ink
{
    // Quick picks for the text tool (generated: non-ASCII characters are escaped). Windows' own panel (Win + .) has the rest.
    internal static class InkSymbols
    {
        public static readonly string[] Emoji =
        {
            "\uD83D\uDE00", "\uD83D\uDE03", "\uD83D\uDE04", "\uD83D\uDE01", "\uD83D\uDE06", "\uD83D\uDE05", "\uD83D\uDE02", "\uD83E\uDD23",
            "\uD83D\uDE0A", "\uD83D\uDE07", "\uD83D\uDE42", "\uD83D\uDE09", "\uD83D\uDE0D", "\uD83E\uDD70", "\uD83D\uDE18", "\uD83D\uDE0B",
            "\uD83D\uDE1B", "\uD83D\uDE1C", "\uD83E\uDD2A", "\uD83D\uDE0E", "\uD83E\uDD29", "\uD83E\uDD73", "\uD83D\uDE0F", "\uD83E\uDD14",
            "\uD83E\uDD17", "\uD83E\uDD2D", "\uD83D\uDE44", "\uD83D\uDE2C", "\uD83D\uDE2E", "\uD83D\uDE32", "\uD83D\uDE33", "\uD83E\uDD7A",
            "\uD83D\uDE22", "\uD83D\uDE2D", "\uD83D\uDE24", "\uD83D\uDE21", "\uD83E\uDD2F", "\uD83D\uDE31", "\uD83D\uDE34", "\uD83E\uDD12",
            "\uD83E\uDD20", "\uD83D\uDE08", "\uD83D\uDC7B", "\uD83D\uDC80", "\uD83D\uDC7D", "\uD83E\uDD16", "\uD83D\uDCA9", "\uD83D\uDE3A",
            "\uD83D\uDE3B", "\uD83D\uDE40", "\uD83D\uDC31", "\uD83D\uDC36", "\uD83D\uDC30", "\uD83D\uDC3B", "\uD83D\uDC3C", "\uD83E\uDD8A",
            "\uD83D\uDC38", "\uD83D\uDC27", "\uD83D\uDC25", "\uD83E\uDD84", "\uD83D\uDC1D", "\uD83E\uDD8B", "\uD83D\uDC22", "\uD83D\uDC19",
            "\uD83C\uDF38", "\uD83C\uDF37", "\uD83C\uDF39", "\uD83C\uDF3B", "\uD83C\uDF40", "\uD83C\uDF08", "\u2600\uFE0F", "\u2B50",
            "\uD83C\uDF19", "\u2601\uFE0F", "\u26A1", "\uD83D\uDD25", "\u2744\uFE0F", "\uD83D\uDCA7", "\uD83C\uDF0A", "\uD83C\uDF55",
            "\uD83C\uDF54", "\uD83C\uDF5F", "\uD83C\uDF69", "\uD83C\uDF6A", "\uD83C\uDF82", "\uD83C\uDF70", "\uD83C\uDF53", "\uD83C\uDF49",
            "\uD83C\uDF51", "\uD83C\uDF52", "\u2615", "\uD83E\uDDCB", "\uD83C\uDF75", "\uD83C\uDF89", "\uD83C\uDF88", "\uD83C\uDF81",
            "\uD83C\uDF80", "\u2728", "\uD83D\uDC96", "\uD83D\uDC95", "\uD83D\uDC97", "\uD83D\uDC9D", "\u2764\uFE0F", "\uD83E\uDDE1",
            "\uD83D\uDC9B", "\uD83D\uDC9A", "\uD83D\uDC99", "\uD83D\uDC9C", "\uD83D\uDDA4", "\uD83E\uDD0D", "\uD83D\uDC94", "\uD83D\uDC4D",
            "\uD83D\uDC4E", "\uD83D\uDC4F", "\uD83D\uDE4C", "\uD83D\uDE4F", "\uD83D\uDC4B", "\u270C\uFE0F", "\uD83E\uDD1E", "\uD83E\uDD1D",
            "\uD83D\uDCAA", "\uD83D\uDC40", "\uD83E\uDDE0", "\uD83D\uDCAF", "\u2705", "\u274C", "\u26A0\uFE0F", "\u2753",
            "\u2757", "\uD83D\uDCA1", "\uD83D\uDCCC", "\uD83D\uDCCE", "\uD83D\uDCDD", "\uD83D\uDCDA", "\u270F\uFE0F", "\uD83D\uDCC5",
            "\u23F0", "\uD83D\uDD14", "\uD83C\uDFB5", "\uD83C\uDFB6", "\uD83C\uDFAE", "\uD83C\uDFC6", "\u26BD", "\uD83D\uDE80",
            "\u2708\uFE0F", "\uD83C\uDFE0", "\uD83C\uDF0D", "\uD83D\uDCA4", "\uD83D\uDCB0", "\uD83D\uDD11",
        };
        public static readonly string[] Kaomoji =
        {
            "(\u25D5\u203F\u25D5)", "(\uFF61\u25D5\u203F\u25D5\uFF61)", "(\u25E0\u203F\u25E0\u273F)", "(\uFF3E\u25BD\uFF3E)", "(\u2267\u25BD\u2266)", "(*^\u25BD^*)", "\u30FD(\u2022\u203F\u2022)\u30CE", "(\u30CE\u25D5\u30EE\u25D5)\u30CE*:\uFF65\uFF9F\u2727",
            "(\u3065\uFF61\u25D5\u203F\u203F\u25D5\uFF61)\u3065", "\u0295\u2022\u1D25\u2022\u0294", "(=^\uFF65\u03C9\uFF65^=)", "(\u1D54\u1D25\u1D54)", "(\u00B4\u2022 \u03C9 \u2022`)", "(\u2727\u03C9\u2727)", "\u2661(\u02C6\u25BD\u02C6)\u2661", "(\u2665\u03C9\u2665*)",
            "( \u02D8 \u00B3\u02D8)\u2665", "(\u3063\u02D8\u03C9\u02D8\u03C2)", "(\uFF5E\uFFE3\u25BD\uFFE3)\uFF5E", "(\uFFE3\u03C9\uFFE3)", "(o^\u25BD^o)", "\\(^o^)/", "(^_^)v", "\u266A(\u00B4\u25BD\uFF40)",
            "(\uFF61\u2022\u0301\uFE3F\u2022\u0300\uFF61)", "(\u2565\uFE4F\u2565)", "(T_T)", "(>_<)", "(\u30FB_\u30FB;)", "(\u2299_\u2299)", "(\u00B0\u30ED\u00B0) !", "(\u00AC\u203F\u00AC)",
            "(\u00AC_\u00AC)", "(\u0CA0_\u0CA0)", "\u00AF\\_(\u30C4)_/\u00AF", "(\u2022_\u2022)", "(\u2310\u25A0_\u25A0)", "(\u0E07 \u2022\u0300_\u2022\u0301)\u0E07", "(\u261E\uFF9F\u30EE\uFF9F)\u261E", "(\u256F\u00B0\u25A1\u00B0)\u256F\uFE35 \u253B\u2501\u253B",
            "\u252C\u2500\u252C\u30CE( \u00BA _ \u00BA\u30CE)", "(=\u2180\u03C9\u2180=)", "(\u15D2\u15E8\u15D5)", "(\u00B4-\u03C9-`)", "zzz (\uFF0D_\uFF0D)",
        };
        public static readonly string[] Symbols =
        {
            "\u2605", "\u2606", "\u2726", "\u2727", "\u2729", "\u272A", "\u2764", "\u2665",
            "\u2661", "\u2765", "\u273F", "\u2740", "\u2741", "\u273E", "\u2600", "\u2601",
            "\u2602", "\u2603", "\u2604", "\u26A1", "\u2744", "\u263E", "\u263D", "\u2713",
            "\u2714", "\u2717", "\u2718", "\u2715", "\u2610", "\u2611", "\u2612", "\u2192",
            "\u2190", "\u2191", "\u2193", "\u2194", "\u2195", "\u21D2", "\u21D0", "\u21D1",
            "\u21D3", "\u279C", "\u27A4", "\u21BB", "\u21BA", "\u2022", "\u25E6", "\u25AA",
            "\u25AB", "\u25A0", "\u25A1", "\u25B2", "\u25B3", "\u25BC", "\u25BD", "\u25C6",
            "\u25C7", "\u25CB", "\u25CF", "\u25CE", "\u221E", "\u2248", "\u2260", "\u2264",
            "\u2265", "\u00B1", "\u00D7", "\u00F7", "\u00B0", "\u221A", "\u2211", "\u03C0",
            "\u2206", "\u03A9", "\u00B5", "\u20AC", "\u00A3", "\u00A5", "\u00A2", "\u00A9",
            "\u00AE", "\u2122", "\u00A7", "\u00B6", "\u2020", "\u2026", "\u203C", "\u2049",
            "\u266A", "\u266B", "\u2669", "\u266C", "\u263A", "\u263B", "\u270C", "\u261D",
            "\u270D", "\u2709", "\u260E", "\u231B", "\u23F3", "\u2702", "\u270F", "\u2712",
            "\u2691", "\u2690", "\u2693", "\u2699", "\u2696", "\u26A0", "\u2615", "\u2618",
            "\u267B", "\u26BD", "\u2660", "\u2663", "\u2666", "\u265B", "\u265A", "\u262F",
            "\u262E",
        };
    }
}
