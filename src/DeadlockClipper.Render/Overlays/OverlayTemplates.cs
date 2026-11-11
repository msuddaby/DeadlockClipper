using System.Net;

namespace DeadlockClipper.Render.Overlays;

/// <summary>Hero art and fonts available to the templates (file paths; null when the game doesn't provide one).</summary>
public sealed record OverlayArt(
    string? PlayerCard,
    string? PlayerGloat,
    string? PlayerLogoSvg,
    string PlayerHeroName,
    string? FontsDir,
    IReadOnlyList<(string? Card, string HeroName)> Victims);

/// <summary>
/// HTML for each overlay graphic, laid out on a full 1920x1080 transparent page at its on-screen position (clear of
/// the kill feed, health and ability HUD). Rendered to PNG by headless Chromium. Ported from the approved mockups.
/// </summary>
public static class OverlayTemplates
{
    private const string Gold = "#ffcf5a";

    public static string Intro(RenderJob job, OverlayArt art) => Page(art, $$"""
        .card { left: 40px; top: 300px; width: 820px; height: 250px; }
        .card .bg { position:absolute; inset: 40px 0 0 70px; background: linear-gradient(100deg, rgba(14,12,10,.92) 60%, rgba(14,12,10,0));
                    clip-path: polygon(0 0, 100% 0, 92% 100%, 0 100%); border-left: 6px solid {{Gold}}; }
        .card .por { position:absolute; left: 0; bottom: 0; height: 250px; filter: drop-shadow(0 6px 18px rgba(0,0,0,.6)); }
        .card .txt { position:absolute; left: 215px; top: 66px; color: #fff; }
        .card .hero { height: 46px; filter: brightness(0) invert(1); }
        .card .heroname { font: 700 42px Reaver, sans-serif; letter-spacing: 2px; text-transform: uppercase; line-height: 46px; }
        .card .pl { white-space: nowrap; font: 700 40px Reaver, sans-serif; letter-spacing: .5px; margin-top: 8px; text-shadow: 0 2px 6px #000; }
        .card .sub { white-space: nowrap; font: 600 20px Jost, sans-serif; color: {{Gold}}; letter-spacing: 1.5px; text-transform: uppercase; margin-top: 6px; }
        """, $"""
        <div class="abs card"><div class="bg"></div>{Img(art.PlayerCard, "por")}
          <div class="txt">{(art.PlayerLogoSvg != null ? Img(art.PlayerLogoSvg, "hero") : $"<div class=\"heroname\">{E(art.PlayerHeroName)}</div>")}
            <div class="pl">{E(job.PlayerName)}</div><div class="sub">{E(IntroDetails(job, art))}</div></div></div>
        """);

    /// <summary>The subtitle minus the hero name, which the card already shows as the hero logo.</summary>
    internal static string IntroDetails(RenderJob job, OverlayArt art)
    {
        var parts = job.IntroSubtitle.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" · ", parts.Where(p => !string.Equals(p, art.PlayerHeroName, StringComparison.OrdinalIgnoreCase)));
    }

    public static string Kill(RenderJob job, OverlayArt art, int index)
    {
        var kill = job.KillMarks.OrderBy(k => k.OffsetSeconds).ElementAt(index);
        var victim = index < art.Victims.Count ? art.Victims[index] : (null, "");
        var who = string.IsNullOrWhiteSpace(victim.HeroName) ? kill.VictimName : $"{kill.VictimName} · {victim.HeroName}";
        return Page(art, """
            .slash { left: -60px; right: 900px; top: 600px; height: 150px; transform: skewY(-6deg);
                     background: linear-gradient(90deg, rgba(200,30,30,0), rgba(200,30,30,.88) 18%, rgba(120,10,10,.88) 70%, rgba(120,10,10,0)); }
            .slashtxt { left: 120px; top: 586px; display:flex; align-items:center; gap: 26px; color:#fff; transform: rotate(-6deg); }
            .slashtxt .big { font: 900 150px Jost, sans-serif; line-height: 1; text-shadow: 0 6px 20px rgba(0,0,0,.6); }
            .slashtxt .k { font: 700 64px Reaver, sans-serif; letter-spacing: 6px; }
            .slashtxt .v { font: 600 26px Jost, sans-serif; text-transform: uppercase; letter-spacing: 2px; opacity: .9; white-space: nowrap; }
            .slashtxt img { height: 170px; filter: grayscale(.3) drop-shadow(0 6px 16px #000); }
            """, $"""
            <div class="abs slash"></div><div class="abs slashtxt"><div class="big">{index + 1}</div>
              <div><div class="k">KILL</div><div class="v">{E(who)}</div></div>{Img(victim.Card)}</div>
            """);
    }

    public static string Final(RenderJob job, OverlayArt art)
    {
        var words = job.HighlightLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(E);
        return Page(art, $$"""
            .hypefinal { left:0; right:0; top: 120px; height: 420px; }
            .hypefinal .t { position:absolute; right: 470px; top: 70px; text-align: right; font: 700 150px Reaver, sans-serif; line-height: .9;
                            transform: skewX(-10deg); background: linear-gradient(180deg, #fff6d5, {{Gold}} 45%, #c07d10);
                            -webkit-background-clip: text; background-clip: text; color: transparent;
                            filter: drop-shadow(0 8px 0 #2a1600) drop-shadow(0 14px 30px rgba(0,0,0,.7)); }
            .hypefinal img { position:absolute; right: 60px; top: 0; height: 430px; filter: drop-shadow(0 10px 30px #000); }
            .hypefinal .who { position:absolute; right: 500px; top: 330px; font: 600 30px Jost, sans-serif; color: #fff; letter-spacing: 3px;
                              text-transform: uppercase; text-shadow: 0 2px 8px #000; white-space: nowrap; }
            """, $"""
            <div class="abs hypefinal">{Img(art.PlayerGloat ?? art.PlayerCard)}<div class="t">{string.Join("<br>", words)}</div>
              <div class="who">{E(job.PlayerName)} · {E(art.PlayerHeroName)}</div></div>
            """);
    }

    private static string Page(OverlayArt art, string css, string body)
    {
        var fonts = art.FontsDir == null ? "" : $$"""
            @font-face { font-family: Reaver; src: url({{Uri(Path.Combine(art.FontsDir, "reaver-bold.otf"))}}); font-weight: 700; }
            @font-face { font-family: Jost; src: url({{Uri(Path.Combine(art.FontsDir, "jost-semibold.ttf"))}}); font-weight: 600; }
            @font-face { font-family: Jost; src: url({{Uri(Path.Combine(art.FontsDir, "jost-black.ttf"))}}); font-weight: 900; }
            """;
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
            {{fonts}}
            html, body { margin: 0; width: 1920px; height: 1080px; background: transparent; overflow: hidden; }
            * { box-sizing: border-box; }
            .abs { position: absolute; }
            {{css}}
            </style></head><body>{{body}}</body></html>
            """;
    }

    private static string Img(string? path, string? cls = null) =>
        path == null ? "" : $"<img{(cls != null ? $" class=\"{cls}\"" : "")} src=\"{Uri(path)}\">";

    private static string Uri(string path) => new Uri(path).AbsoluteUri;

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
