using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;

namespace Inmobiscrap.Services;

public interface IReportService
{
    Task<byte[]> GenerateMarketReportAsync(ReportFilters filters);
}

public record ReportFilters(
    string? Region       = null,
    string? City         = null,
    string? Neighborhood = null,
    string? PropertyType = null);

public class ReportService : IReportService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ReportService> _logger;

    public ReportService(ApplicationDbContext context, ILogger<ReportService> logger)
    {
        _context = context;
        _logger  = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // ENTRY POINT
    // ══════════════════════════════════════════════════════════════════════

    public async Task<byte[]> GenerateMarketReportAsync(ReportFilters filters)
    {
        var data = await CollectDataAsync(filters);
        var tex  = BuildLatexDocument(data, filters);
        return await CompileLatexAsync(tex);
    }

    // ══════════════════════════════════════════════════════════════════════
    // DATA COLLECTION
    // ══════════════════════════════════════════════════════════════════════

    private async Task<ReportData> CollectDataAsync(ReportFilters f)
    {
        var q = _context.Properties.AsQueryable();

        if (!string.IsNullOrWhiteSpace(f.Region))       q = q.Where(p => p.Region       == f.Region);
        if (!string.IsNullOrWhiteSpace(f.City))         q = q.Where(p => p.City         == f.City);
        if (!string.IsNullOrWhiteSpace(f.Neighborhood)) q = q.Where(p => p.Neighborhood == f.Neighborhood);
        if (!string.IsNullOrWhiteSpace(f.PropertyType)) q = q.Where(p => p.PropertyType == f.PropertyType);

        var total = await q.CountAsync();

        // ── Por tipo + moneda ─────────────────────────────────────────────────
        var byTypeRaw = await q
            .Where(p => p.PropertyType != null && p.Price > 0)
            .GroupBy(p => new { p.PropertyType, Currency = p.Currency ?? "CLP" })
            .Select(g => new TypeCurrencyRow
            {
                Type         = g.Key.PropertyType!,
                Currency     = g.Key.Currency,
                Count        = g.Count(),
                AvgPrice     = g.Average(p => (double)p.Price!),
                MinPrice     = g.Min(p => (double)p.Price!),
                MaxPrice     = g.Max(p => (double)p.Price!),
                AvgBedrooms  = g.Where(p => p.Bedrooms  > 0).Any() ? g.Where(p => p.Bedrooms  > 0).Average(p => (double)p.Bedrooms!)  : null,
                AvgBathrooms = g.Where(p => p.Bathrooms > 0).Any() ? g.Where(p => p.Bathrooms > 0).Average(p => (double)p.Bathrooms!) : null,
                AvgArea      = g.Where(p => p.Area      > 0).Any() ? g.Where(p => p.Area      > 0).Average(p => (double)p.Area!)      : null,
            })
            .ToListAsync();

        // Elegir moneda dominante por tipo (igual que AnalyticsController)
        var byType = byTypeRaw
            .GroupBy(r => r.Type)
            .Select(g =>
            {
                var dom = g.OrderByDescending(x => x.Count).First();
                dom.Count = g.Sum(x => x.Count);
                return dom;
            })
            .OrderByDescending(r => r.Count)
            .ToList();

        // ── Top comunas — precios separados por moneda ──────────────────────────
        var neighborhoodRaw = await q
            .Where(p => (p.Neighborhood != null || p.City != null) && p.Price > 0)
            .GroupBy(p => new { Name = p.Neighborhood ?? p.City ?? "Sin dato", Currency = p.Currency ?? "CLP" })
            .Select(g => new { g.Key.Name, g.Key.Currency, Count = g.Count(), Avg = g.Average(p => (double)p.Price!) })
            .ToListAsync();

        var topNeighborhoods = neighborhoodRaw
            .GroupBy(r => r.Name)
            .Select(g => new NeighborhoodRow
            {
                Name        = g.Key,
                Count       = g.Sum(r => r.Count),
                AvgPriceUF  = g.FirstOrDefault(r => r.Currency == "UF")?.Avg,
                AvgPriceCLP = g.FirstOrDefault(r => r.Currency == "CLP")?.Avg,
            })
            .OrderByDescending(r => r.Count)
            .Take(8)
            .ToList();

        // ── Cambios de precio recientes ───────────────────────────────────────
        var recentChanges = (await q
            .Where(p => p.PriceChangedAt != null && p.PreviousPrice != null && p.Price != null)
            .OrderByDescending(p => p.PriceChangedAt)
            .Take(10)
            .Select(p => new
            {
                p.Title,
                City           = p.City ?? "—",
                OldPrice       = (double)p.PreviousPrice!,
                NewPrice       = (double)p.Price!,
                Currency       = p.Currency ?? "CLP",
                PriceChangedAt = p.PriceChangedAt!.Value,
            })
            .ToListAsync())
            .Select(p => new PriceChangeRow
            {
                Title          = p.Title.Length > 55 ? p.Title.Substring(0, 55) + "…" : p.Title,
                City           = p.City,
                OldPrice       = p.OldPrice,
                NewPrice       = p.NewPrice,
                Currency       = p.Currency,
                PriceChangedAt = p.PriceChangedAt,
            })
            .ToList();

        // ── Globales ──────────────────────────────────────────────────────────
        var ufProps  = await q.Where(p => p.Currency == "UF"  && p.Price > 0).Select(p => (double)p.Price!).ToListAsync();
        var clpProps = await q.Where(p => p.Currency == "CLP" && p.Price > 0).Select(p => (double)p.Price!).ToListAsync();

        double? globalAvgUF  = ufProps.Count  > 0 ? ufProps.Average()  : null;
        double? globalAvgCLP = clpProps.Count > 0 ? clpProps.Average() : null;

        // ── Tracking stats ────────────────────────────────────────────────────
        var newLast7 = await q
            .CountAsync(p => p.FirstSeenAt != null && p.FirstSeenAt >= DateTime.UtcNow.AddDays(-7));
        var withChanges = await q.CountAsync(p => p.PreviousPrice != null);

        return new ReportData
        {
            Total            = total,
            ByType           = byType,
            TopNeighborhoods = topNeighborhoods,
            RecentChanges    = recentChanges,
            GlobalAvgUF      = globalAvgUF,
            GlobalAvgCLP     = globalAvgCLP,
            NewLast7Days     = newLast7,
            WithPriceChanges = withChanges,
            GeneratedAt      = DateTime.Now,
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // LATEX BUILDER
    // ══════════════════════════════════════════════════════════════════════

    private static string BuildLatexDocument(ReportData d, ReportFilters f)
    {
        var sb = new StringBuilder();

        // ── Título dinámico ───────────────────────────────────────────────────
        var scope = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Region))       scope.Add(Esc(f.Region!));
        if (!string.IsNullOrWhiteSpace(f.City))         scope.Add(Esc(f.City!));
        if (!string.IsNullOrWhiteSpace(f.Neighborhood)) scope.Add(Esc(f.Neighborhood!));
        if (!string.IsNullOrWhiteSpace(f.PropertyType)) scope.Add(Esc(f.PropertyType!));
        var scopeStr = scope.Count > 0 ? string.Join(" $\\cdot$ ", scope) : "Mercado General";

        sb.AppendLine(@"\documentclass[11pt,a4paper]{article}

% ── Encoding & idioma ──────────────────────────────────────────────────────────
\usepackage[utf8]{inputenc}
\usepackage[T1]{fontenc}
\usepackage[spanish]{babel}

% ── Tipografía ─────────────────────────────────────────────────────────────────
\usepackage{lmodern}
\usepackage{microtype}

% ── Layout ────────────────────────────────────────────────────────────────────
\usepackage[top=2.2cm,bottom=2.2cm,left=2.5cm,right=2.5cm]{geometry}
\usepackage{parskip}

% ── Colores ───────────────────────────────────────────────────────────────────
\usepackage[table]{xcolor}
\definecolor{primary}{HTML}{0F4C81}
\definecolor{accent}{HTML}{2EC4B6}
\definecolor{lightbg}{HTML}{F0F4F8}
\definecolor{muted}{HTML}{6C757D}
\definecolor{up}{HTML}{16A34A}
\definecolor{down}{HTML}{DC2626}

% ── Tablas ────────────────────────────────────────────────────────────────────
\usepackage{booktabs}
\usepackage{tabularx}
\usepackage{multirow}
\usepackage{array}
\newcolumntype{R}[1]{>{\raggedleft\arraybackslash}p{#1}}
\newcolumntype{C}[1]{>{\centering\arraybackslash}p{#1}}
\newcolumntype{L}[1]{>{\raggedright\arraybackslash}p{#1}}

% ── Cabecera / pie ─────────────────────────────────────────────────────────────
\usepackage{fancyhdr}
\pagestyle{fancy}
\fancyhf{}
\renewcommand{\headrulewidth}{0.4pt}
\fancyhead[L]{\small\color{muted}InmobiScrap}
\fancyhead[R]{\small\color{muted}" + Esc(d.GeneratedAt.ToString("dd/MM/yyyy HH:mm")) + @"}
\fancyfoot[C]{\small\color{muted}\thepage}

% ── Título personalizado ───────────────────────────────────────────────────────
\usepackage{titlesec}
\titleformat{\section}{\large\bfseries\color{primary}}{}{0em}{}[\titlerule]
\titleformat{\subsection}{\normalsize\bfseries\color{primary}}{}{0em}{}

% ── Misc ───────────────────────────────────────────────────────────────────────
\usepackage{graphicx}
\usepackage{enumitem}
\usepackage{calc}

%% Comando para KPI card ────────────────────────────────────────────────────────
\newcommand{\kpicard}[3]{%
  \begin{minipage}[t]{0.30\linewidth}
    \centering
    \colorbox{lightbg}{\parbox{\linewidth-2\fboxsep}{%
      \centering\vspace{4pt}
      {\scriptsize\color{muted} #1}\\[2pt]
      {\large\bfseries\color{primary} #2}\\[2pt]
      {\scriptsize\color{muted} #3}
      \vspace{4pt}
    }}
  \end{minipage}%
}

\begin{document}

% ─────────────────────────────────────────────────────────────────────────────
%  PORTADA
% ─────────────────────────────────────────────────────────────────────────────
\begin{center}
  {\Huge\bfseries\color{primary} InmobiScrap}\\[6pt]
  {\large\color{muted} Informe de Mercado Inmobiliario}\\[4pt]
  {\normalsize\color{muted} " + scopeStr + @"}\\[8pt]
  \textcolor{muted}{\rule{\linewidth}{0.6pt}}\\[4pt]
  {\small\color{muted} Generado el " + Esc(d.GeneratedAt.ToString("dddd, dd 'de' MMMM 'de' yyyy", new System.Globalization.CultureInfo("es-CL"))) + @"}
\end{center}

\vspace{0.5em}

% ─────────────────────────────────────────────────────────────────────────────
%  KPI CARDS
% ─────────────────────────────────────────────────────────────────────────────
\noindent
\kpicard{Total Propiedades}{" + d.Total.ToString("N0") + @"}{en la selección}%
\hfill
\kpicard{Nuevas (últ. 7 días)}{" + (d.NewLast7Days == d.Total ? "---" : "+" + d.NewLast7Days.ToString("N0")) + @"}{añadidas vs hace 7 días}%
\hfill
\kpicard{Con cambio de precio}{" + d.WithPriceChanges.ToString("N0") + @"}{detectados}

\vspace{1em}

\noindent
" + BuildGlobalAvgCards(d) + @"

\vspace{1.5em}

% ─────────────────────────────────────────────────────────────────────────────
%  PROMEDIOS POR TIPO
% ─────────────────────────────────────────────────────────────────────────────
\section{Promedios por Tipo de Propiedad}

\rowcolors{2}{lightbg}{white}
\begin{tabularx}{\linewidth}{L{2.6cm} C{1.2cm} C{1.0cm} C{1.0cm} C{1.0cm} C{1.0cm} R{2.4cm} R{3.0cm}}
  \toprule
  \rowcolor{primary}
  \textcolor{white}{\textbf{Tipo}} &
  \textcolor{white}{\textbf{Cant.}} &
  \textcolor{white}{\textbf{Mon.}} &
  \textcolor{white}{\textbf{Dorm.}} &
  \textcolor{white}{\textbf{Baños}} &
  \textcolor{white}{\textbf{m²}} &
  \textcolor{white}{\textbf{Precio Prom.}} &
  \textcolor{white}{\textbf{Rango}} \\
  \midrule
");

        var ufTypes  = d.ByType.Where(t => t.Currency.ToUpper() == "UF").ToList();
        var clpTypes = d.ByType.Where(t => t.Currency.ToUpper() != "UF").ToList();
        var allGroups = new List<(string label, List<TypeCurrencyRow> rows)>();
        if (ufTypes.Count  > 0) allGroups.Add(("UF",  ufTypes));
        if (clpTypes.Count > 0) allGroups.Add(("CLP", clpTypes));

        bool firstGroup = true;
        foreach (var (label, rows) in allGroups)
        {
            if (!firstGroup)
                sb.AppendLine(@"  \midrule");
            firstGroup = false;
            sb.AppendLine(@"  \multicolumn{8}{l}{\small\color{muted}\textit{Propiedades en " + label + @"}} \\");

            foreach (var t in rows)
            {
                var avgPriceStr = FormatPrice(t.AvgPrice, t.Currency);
                var rangeStr    = t.MinPrice.HasValue && t.MaxPrice.HasValue
                    ? $"{FormatPrice(t.MinPrice, t.Currency)} -- {FormatPrice(t.MaxPrice, t.Currency)}"
                    : "---";
                var dormStr  = t.AvgBedrooms.HasValue  ? t.AvgBedrooms.Value.ToString("F1")  : "---";
                var bathStr  = t.AvgBathrooms.HasValue ? t.AvgBathrooms.Value.ToString("F1") : "---";
                var areaStr  = t.AvgArea.HasValue      ? $"{t.AvgArea.Value:F0} m\\textsuperscript{{2}}" : "---";

                var row = $"  {Esc(t.Type)} & {t.Count:N0} & {Esc(t.Currency)} & {dormStr} & {bathStr} & {areaStr} & {avgPriceStr} & " +
                          @"{\small} " + rangeStr + @" \\";
                sb.AppendLine(row);
            }
        }

        sb.AppendLine(@"  \bottomrule
\end{tabularx}

\vspace{1.5em}

% ─────────────────────────────────────────────────────────────────────────────
%  TOP COMUNAS
% ─────────────────────────────────────────────────────────────────────────────
\section{Top Comunas / Ciudades por Cantidad de Propiedades}

\rowcolors{2}{lightbg}{white}
\begin{tabularx}{\linewidth}{L{4.5cm} C{2cm} R{3cm} R{3cm}}
  \toprule
  \rowcolor{primary}
  \textcolor{white}{\textbf{Comuna / Ciudad}} &
  \textcolor{white}{\textbf{Propiedades}} &
  \textcolor{white}{\textbf{Precio Prom. (UF)}} &
  \textcolor{white}{\textbf{Precio Prom. (CLP)}} \\
  \midrule
");

        foreach (var n in d.TopNeighborhoods)
        {
            var ufStr  = n.AvgPriceUF.HasValue  ? $"{n.AvgPriceUF.Value:N0} UF"            : "---";
            var clpStr = n.AvgPriceCLP.HasValue ? FormatPrice(n.AvgPriceCLP.Value, "CLP") : "---";
            sb.AppendLine($"  {Esc(n.Name)} & {n.Count:N0} & {ufStr} & {clpStr} \\\\");
        }

        sb.AppendLine(@"  \bottomrule
\end{tabularx}

\vspace{1.5em}");

        // ── Cambios de precio recientes ───────────────────────────────────────
        if (d.RecentChanges.Count > 0)
        {
            sb.AppendLine(@"
% ─────────────────────────────────────────────────────────────────────────────
%  CAMBIOS DE PRECIO RECIENTES
% ─────────────────────────────────────────────────────────────────────────────
\section{Cambios de Precio Recientes}

\rowcolors{2}{lightbg}{white}
\begin{tabularx}{\linewidth}{L{5.5cm} C{1.8cm} R{2.3cm} R{2.3cm} R{2.3cm}}
  \toprule
  \rowcolor{primary}
  \textcolor{white}{\textbf{Propiedad}} &
  \textcolor{white}{\textbf{Ciudad}} &
  \textcolor{white}{\textbf{Precio Ant.}} &
  \textcolor{white}{\textbf{Precio Act.}} &
  \textcolor{white}{\textbf{Variación}} \\
  \midrule
");
            foreach (var c in d.RecentChanges)
            {
                var diff    = c.NewPrice - c.OldPrice;
                var diffPct = c.OldPrice > 0 ? diff / c.OldPrice * 100 : 0;
                var color   = diff >= 0 ? "up" : "down";
                var sign    = diff >= 0 ? "+" : "";
                var varStr  = $@"\textcolor{{{color}}}{{{sign}{diffPct:F1}\%}}";
                sb.AppendLine(
                    $"  {Esc(c.Title)} & {Esc(c.City)} & {FormatPrice(c.OldPrice, c.Currency)} & " +
                    $"{FormatPrice(c.NewPrice, c.Currency)} & {varStr} \\\\");
            }

            sb.AppendLine(@"  \bottomrule
\end{tabularx}
");
        }

        // ── Nota al pie ───────────────────────────────────────────────────────
        sb.AppendLine(@"
\vfill
\textcolor{muted}{\rule{\linewidth}{0.4pt}}\\
{\scriptsize\color{muted}
  Este informe fue generado automáticamente por \textbf{InmobiScrap}.
  Los datos provienen de scraping de portales inmobiliarios chilenos y pueden contener imprecisiones.
  Todos los precios en UF y CLP corresponden a la moneda indicada por la fuente original.
}

\end{document}");

        return sb.ToString();
    }

    private static string BuildGlobalAvgCards(ReportData d)
    {
        var cards = new List<string>();

        if (d.GlobalAvgUF.HasValue)
            cards.Add($@"\kpicard{{Precio Promedio (UF)}}{{{d.GlobalAvgUF.Value:F0} UF}}{{promedio general}}");

        if (d.GlobalAvgCLP.HasValue)
        {
            var clpM = d.GlobalAvgCLP.Value / 1_000_000;
            cards.Add($@"\kpicard{{Precio Promedio (CLP)}}{{\${clpM:F1}M}}{{promedio general}}");
        }

        if (cards.Count == 0) return string.Empty;
        if (cards.Count == 1) return cards[0];

        return string.Join(@"\hfill" + Environment.NewLine, cards);
    }

    // ── Escape caracteres especiales de LaTeX ─────────────────────────────────
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "---";
        return s
            .Replace(@"\", @"\textbackslash{}")
            .Replace("&",  @"\&")
            .Replace("%",  @"\%")
            .Replace("$",  @"\$")
            .Replace("#",  @"\#")
            .Replace("_",  @"\_")
            .Replace("{",  @"\{")
            .Replace("}",  @"\}")
            .Replace("~",  @"\textasciitilde{}")
            .Replace("^",  @"\textasciicircum{}")
            .Replace("…",  @"\ldots{}");
    }

    private static string FormatPrice(double? value, string? currency)
    {
        if (value == null) return "---";
        if ((currency ?? "").ToUpper() == "UF")
            return $"{value.Value:N0} UF";
        if (value >= 1_000_000_000)
            return $@"\${value.Value / 1_000_000_000:F1}B";
        if (value >= 1_000_000)
            return $@"\${value.Value / 1_000_000:F0}M";
        if (value >= 1_000)
            return $@"\${value.Value / 1_000:F0}K";
        return $@"\${value.Value:N0}";
    }

    // ══════════════════════════════════════════════════════════════════════
    // LATEX → PDF COMPILATION
    // ══════════════════════════════════════════════════════════════════════

    private async Task<byte[]> CompileLatexAsync(string latexSource)
    {
        // Directorio temporal aislado por compilación
        var workDir = Path.Combine(Path.GetTempPath(), $"inmobiscrap-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var texFile = Path.Combine(workDir, "report.tex");
        var pdfFile = Path.Combine(workDir, "report.pdf");

        try
        {
            await File.WriteAllTextAsync(texFile, latexSource, Encoding.UTF8);

            // pdflatex requiere 2 pasadas para referencias internas
            for (int pass = 1; pass <= 2; pass++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "pdflatex",
                    Arguments              = $"-interaction=nonstopmode -output-directory={workDir} {texFile}",
                    WorkingDirectory       = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("No se pudo iniciar pdflatex.");

                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();

                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0)
                {
                    _logger.LogError("pdflatex pass {Pass} failed.\nSTDOUT:\n{Out}\nSTDERR:\n{Err}",
                        pass, stdout, stderr);
                    throw new InvalidOperationException(
                        $"pdflatex falló (código {proc.ExitCode}). Revisa los logs del servidor.");
                }

                _logger.LogDebug("pdflatex pass {Pass} OK", pass);
            }

            if (!File.Exists(pdfFile))
                throw new FileNotFoundException("pdflatex no generó el archivo PDF.");

            return await File.ReadAllBytesAsync(pdfFile);
        }
        finally
        {
            // Limpiar archivos temporales siempre
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// DTOs internos del reporte
// ══════════════════════════════════════════════════════════════════════════════

internal class ReportData
{
    public int Total { get; set; }
    public List<TypeCurrencyRow> ByType { get; set; } = new();
    public List<NeighborhoodRow> TopNeighborhoods { get; set; } = new();
    public List<PriceChangeRow>  RecentChanges { get; set; } = new();
    public double? GlobalAvgUF  { get; set; }
    public double? GlobalAvgCLP { get; set; }
    public int NewLast7Days     { get; set; }
    public int WithPriceChanges { get; set; }
    public DateTime GeneratedAt { get; set; }
}

internal class TypeCurrencyRow
{
    public string  Type         { get; set; } = "";
    public string  Currency     { get; set; } = "CLP";
    public int     Count        { get; set; }
    public double? AvgPrice     { get; set; }
    public double? MinPrice     { get; set; }
    public double? MaxPrice     { get; set; }
    public double? AvgBedrooms  { get; set; }
    public double? AvgBathrooms { get; set; }
    public double? AvgArea      { get; set; }
}

internal class NeighborhoodRow
{
    public string  Name        { get; set; } = "";
    public int     Count       { get; set; }
    public double? AvgPriceUF  { get; set; }   // null si no hay propiedades UF en esta comuna
    public double? AvgPriceCLP { get; set; }   // null si no hay propiedades CLP
}

internal class PriceChangeRow
{
    public string   Title          { get; set; } = "";
    public string   City           { get; set; } = "";
    public double   OldPrice       { get; set; }
    public double   NewPrice       { get; set; }
    public string   Currency       { get; set; } = "CLP";
    public DateTime PriceChangedAt { get; set; }
}