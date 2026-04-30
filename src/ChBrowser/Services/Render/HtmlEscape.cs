using System.Text;

namespace ChBrowser.Services.Render;

internal static class HtmlEscape
{
    public static string Text(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&':  sb.Append("&amp;");  break;
                case '<':  sb.Append("&lt;");   break;
                case '>':  sb.Append("&gt;");   break;
                case '"':  sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;");  break;
                default:   sb.Append(c);        break;
            }
        }
        return sb.ToString();
    }

    public static string Attr(string s) => Text(s);
}
