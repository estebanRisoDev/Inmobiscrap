using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Inmobiscrap.Data;

namespace Inmobiscrap.Services;

public interface IReportService
{
    Task<byte[]> GenerateMarketReportAsync(ReportFilters filters);
    Task<byte[]> GenerateComparisonReportAsync(List<int> propertyIds);
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

    public async Task<byte[]> GenerateComparisonReportAsync(List<int> propertyIds)
    {
        var properties = await _context.Properties
            .Where(p => propertyIds.Contains(p.Id))
            .ToListAsync();

        if (properties.Count < 2)
            throw new InvalidOperationException("Se necesitan al menos 2 propiedades para comparar.");

        var tex = BuildComparisonLatex(properties);
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

    private static string BuildComparisonLatex(List<Inmobiscrap.Models.Property> properties)
    {
        var sb = new StringBuilder();
        var now = DateTime.Now;

        sb.AppendLine(@"\documentclass[11pt,a4paper]{article}

% ── Encoding & idioma ──────────────────────────────────────────────────────────
\usepackage[utf8]{inputenc}
\usepackage[T1]{fontenc}
\usepackage[spanish]{babel}

% ── Tipografía ─────────────────────────────────────────────────────────────────
\usepackage{lmodern}
\usepackage{microtype}

% ── Layout ────────────────────────────────────────────────────────────────────
\usepackage[landscape,top=1.8cm,bottom=1.8cm,left=1.5cm,right=1.5cm]{geometry}
\usepackage{parskip}

% ── Colores ───────────────────────────────────────────────────────────────────
\usepackage[table]{xcolor}
\definecolor{primary}{HTML}{0F4C81}
\definecolor{accent}{HTML}{2EC4B6}
\definecolor{lightbg}{HTML}{F0F4F8}
\definecolor{muted}{HTML}{6C757D}
\definecolor{up}{HTML}{16A34A}
\definecolor{down}{HTML}{DC2626}
\definecolor{nuevo}{HTML}{2A9D8F}
\definecolor{usado}{HTML}{F4A261}

% ── Tablas ────────────────────────────────────────────────────────────────────
\usepackage{booktabs}
\usepackage{tabularx}
\usepackage{array}
\usepackage{longtable}
\newcolumntype{R}[1]{>{\raggedleft\arraybackslash}p{#1}}
\newcolumntype{C}[1]{>{\centering\arraybackslash}p{#1}}
\newcolumntype{L}[1]{>{\raggedright\arraybackslash}p{#1}}

% ── Gráficos ─────────────────────────────────────────────────────────────────
\usepackage{pgfplots}
\pgfplotsset{compat=1.18}

% ── Cabecera / pie ─────────────────────────────────────────────────────────────
\usepackage{fancyhdr}
\pagestyle{fancy}
\fancyhf{}
\renewcommand{\headrulewidth}{0.4pt}
\fancyhead[L]{\small\color{muted}InmobiScrap --- Comparativa}
\fancyhead[R]{\small\color{muted}" + Esc(now.ToString("dd/MM/yyyy HH:mm")) + @"}
\fancyfoot[C]{\small\color{muted}\thepage}

% ── Título personalizado ───────────────────────────────────────────────────────
\usepackage{titlesec}
\titleformat{\section}{\large\bfseries\color{primary}}{}{0em}{}[\titlerule]
\titleformat{\subsection}{\normalsize\bfseries\color{primary}}{}{0em}{}

\usepackage{graphicx}
\usepackage{calc}
\usepackage{enumitem}

\begin{document}

% ─────────────────────────────────────────────────────────────────────────────
%  PORTADA
% ─────────────────────────────────────────────────────────────────────────────
\begin{center}
  {\Huge\bfseries\color{primary} InmobiScrap}\\[6pt]
  {\large\color{muted} Informe Comparativo de Propiedades}\\[4pt]
  {\normalsize\color{muted} " + properties.Count + @" propiedades seleccionadas}\\[8pt]
  \textcolor{muted}{\rule{\linewidth}{0.6pt}}\\[4pt]
  {\small\color{muted} Generado el " + Esc(now.ToString("dddd, dd 'de' MMMM 'de' yyyy", new System.Globalization.CultureInfo("es-CL"))) + @"}
\end{center}

\vspace{1em}

% ─────────────────────────────────────────────────────────────────────────────
%  TABLAS COMPARATIVAS
% ─────────────────────────────────────────────────────────────────────────────
\section{Comparación de Propiedades}
");

        // Split into chunks of up to 5 properties per table
        const int chunkSize = 5;
        var chunks = new List<List<Inmobiscrap.Models.Property>>();
        for (int c = 0; c < properties.Count; c += chunkSize)
            chunks.Add(properties.Skip(c).Take(chunkSize).ToList());

        for (int ci = 0; ci < chunks.Count; ci++)
        {
            var chunk = chunks[ci];
            var startIdx = ci * chunkSize; // global index offset

            if (ci > 0)
                sb.AppendLine(@"\vspace{1em}");

            if (chunks.Count > 1)
                sb.AppendLine($@"\subsection{{Propiedades {startIdx + 1} a {startIdx + chunk.Count}}}");

            var colWidth = Math.Max(3.0, 22.0 / chunk.Count);
            var colSpec = string.Join(" ", chunk.Select(_ =>
                "C{" + colWidth.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "cm}"));

            sb.AppendLine($@"
\rowcolors{{2}}{{lightbg}}{{white}}
\begin{{tabularx}}{{\linewidth}}{{L{{3.2cm}} {colSpec}}}
  \toprule
  \rowcolor{{primary}}
  \textcolor{{white}}{{\textbf{{Atributo}}}}");

            for (int i = 0; i < chunk.Count; i++)
                sb.Append($" & \\textcolor{{white}}{{\\textbf{{P{startIdx + i + 1}}}}}");
            sb.AppendLine(@" \\
  \midrule");

            // Title
            sb.Append("  \\textbf{Título}");
            foreach (var p in chunk)
            {
                var maxLen = chunk.Count <= 3 ? 40 : 25;
                var title = (p.Title ?? "---").Length > maxLen ? p.Title!.Substring(0, maxLen) + "..." : (p.Title ?? "---");
                sb.Append($" & {{\\small {Esc(title)}}}");
            }
            sb.AppendLine(" \\\\");

            // Price
            sb.Append("  \\textbf{Precio}");
            foreach (var p in chunk)
                sb.Append($" & \\textbf{{{FormatPrice(p.Price.HasValue ? (double)p.Price.Value : (double?)null, p.Currency)}}}");
            sb.AppendLine(" \\\\");

            // Type
            sb.Append("  \\textbf{Tipo}");
            foreach (var p in chunk) sb.Append($" & {Esc(p.PropertyType)}");
            sb.AppendLine(" \\\\");

            // Condition
            sb.Append("  \\textbf{Estado}");
            foreach (var p in chunk)
            {
                var cond = p.Condition ?? "---";
                var color = cond == "Nuevo" ? "nuevo" : cond == "Usado" ? "usado" : "muted";
                sb.Append($" & \\textcolor{{{color}}}{{{Esc(cond)}}}");
            }
            sb.AppendLine(" \\\\");

            // Bedrooms
            sb.Append("  \\textbf{Dormitorios}");
            foreach (var p in chunk) sb.Append($" & {(p.Bedrooms.HasValue ? p.Bedrooms.Value.ToString() : "---")}");
            sb.AppendLine(" \\\\");

            // Bathrooms
            sb.Append("  \\textbf{Baños}");
            foreach (var p in chunk) sb.Append($" & {(p.Bathrooms.HasValue ? p.Bathrooms.Value.ToString() : "---")}");
            sb.AppendLine(" \\\\");

            // Area
            sb.Append("  \\textbf{Superficie}");
            foreach (var p in chunk) sb.Append($" & {(p.Area.HasValue ? $"{p.Area.Value:F0} m\\textsuperscript{{2}}" : "---")}");
            sb.AppendLine(" \\\\");

            // Price per sqm
            sb.Append("  \\textbf{Precio/m\\textsuperscript{2}}");
            foreach (var p in chunk)
            {
                if (p.Price.HasValue && p.Price > 0 && p.Area.HasValue && p.Area > 0)
                {
                    var ppsqm = Math.Round(p.Price.Value / p.Area.Value, 0);
                    sb.Append($" & \\textbf{{{ppsqm:N0} {Esc(p.Currency ?? "CLP")}}}");
                }
                else
                    sb.Append(" & ---");
            }
            sb.AppendLine(" \\\\");

            // City
            sb.Append("  \\textbf{Ciudad}");
            foreach (var p in chunk) sb.Append($" & {Esc(p.City)}");
            sb.AppendLine(" \\\\");

            // Neighborhood
            sb.Append("  \\textbf{Comuna}");
            foreach (var p in chunk) sb.Append($" & {Esc(p.Neighborhood)}");
            sb.AppendLine(" \\\\");

            // Publication Date
            sb.Append("  \\textbf{Publicación}");
            foreach (var p in chunk)
                sb.Append($" & {(p.PublicationDate.HasValue ? p.PublicationDate.Value.ToString("dd/MM/yyyy") : "---")}");
            sb.AppendLine(" \\\\");

            // Days on market
            sb.Append("  \\textbf{Días en mercado}");
            foreach (var p in chunk)
            {
                var days = p.PublicationDate.HasValue ? (int)(DateTime.UtcNow - p.PublicationDate.Value).TotalDays : (int?)null;
                sb.Append($" & {(days.HasValue ? days.Value.ToString() : "---")}");
            }
            sb.AppendLine(" \\\\");

            // First seen
            sb.Append("  \\textbf{Detectada}");
            foreach (var p in chunk)
                sb.Append($" & {(p.FirstSeenAt.HasValue ? p.FirstSeenAt.Value.ToString("dd/MM/yyyy") : "---")}");
            sb.AppendLine(" \\\\");

            // Times scraped
            sb.Append("  \\textbf{Veces detectada}");
            foreach (var p in chunk) sb.Append($" & {p.TimesScraped}");
            sb.AppendLine(" \\\\");

            // Status
            sb.Append("  \\textbf{Estado listado}");
            foreach (var p in chunk)
            {
                var status = p.ListingStatus ?? "---";
                sb.Append($" & {Esc(status)}");
            }
            sb.AppendLine(" \\\\");

            sb.AppendLine(@"  \bottomrule
\end{tabularx}");
        }

        sb.AppendLine(@"\vspace{1.5em}");

        // ══════════════════════════════════════════════════════════════
        // GRÁFICOS COMPARATIVOS
        // ══════════════════════════════════════════════════════════════
        sb.AppendLine(@"\newpage
\section{Gráficos Comparativos}");

        // Define bar colors (cycle through them)
        var barColors = new[] { "primary", "accent", "usado", "nuevo", "muted", "down", "up" };

        // ── Chart 1: Price comparison ──────────────────────────────
        var pricedForChart = properties.Where(p => p.Price.HasValue && p.Price > 0).ToList();
        if (pricedForChart.Count >= 2)
        {
            // Group by currency to make separate charts
            var currencyGroups = pricedForChart.GroupBy(p => (p.Currency ?? "CLP").ToUpper()).ToList();
            foreach (var cg in currencyGroups)
            {
                var items = cg.ToList();
                var maxPrice = items.Max(p => (double)p.Price!);
                var yMax = Math.Ceiling(maxPrice * 1.15);

                sb.AppendLine($@"
\vspace{{0.5em}}
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{Precio ({cg.Key})}}}},
    ybar,
    bar width=0.5cm,
    width=0.9\linewidth,
    height=8cm,
    ymin=0, ymax={yMax.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)},
    symbolic x coords={{{string.Join(",", items.Select((_, i) => $"P{properties.IndexOf(_) + 1}"))}}},
    xtick=data,
    x tick label style={{rotate=45, anchor=east, font=\small}},
    ylabel={{{cg.Key}}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small, /pgf/number format/1000 sep={{\,}}}},
    nodes near coords,
    nodes near coords style={{font=\tiny, rotate=90, anchor=west}},
    every node near coord/.append style={{/pgf/number format/1000 sep={{\,}}}},
    enlarge x limits=0.05,
    grid=major,
    grid style={{dashed, gray!30}},
]
\addplot[fill=primary!80] coordinates {{");
                foreach (var p in items)
                    sb.Append($"(P{properties.IndexOf(p) + 1},{(double)p.Price!:F0}) ");
                sb.AppendLine(@"};
\end{axis}
\end{tikzpicture}
\end{center}");
            }
        }

        // ── Chart 2: Price per m² ──────────────────────────────────
        var withPricePerSqm = properties.Where(p => p.Price > 0 && p.Area > 0).ToList();
        if (withPricePerSqm.Count >= 2)
        {
            var currencyGroups2 = withPricePerSqm.GroupBy(p => (p.Currency ?? "CLP").ToUpper()).ToList();
            foreach (var cg in currencyGroups2)
            {
                var items = cg.ToList();
                var ppsqmValues = items.Select(p => (double)(p.Price!.Value / p.Area!.Value)).ToList();
                var yMax = Math.Ceiling(ppsqmValues.Max() * 1.15);

                sb.AppendLine($@"
\vspace{{0.5em}}
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{Precio/m\textsuperscript{{2}} ({cg.Key})}}}},
    ybar,
    bar width=0.5cm,
    width=0.9\linewidth,
    height=8cm,
    ymin=0, ymax={yMax.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)},
    symbolic x coords={{{string.Join(",", items.Select(p => $"P{properties.IndexOf(p) + 1}"))}}},
    xtick=data,
    x tick label style={{rotate=45, anchor=east, font=\small}},
    ylabel={{{cg.Key}/m\textsuperscript{{2}}}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small, /pgf/number format/1000 sep={{\,}}}},
    nodes near coords,
    nodes near coords style={{font=\tiny, rotate=90, anchor=west}},
    every node near coord/.append style={{/pgf/number format/1000 sep={{\,}}}},
    enlarge x limits=0.05,
    grid=major,
    grid style={{dashed, gray!30}},
]
\addplot[fill=accent!80] coordinates {{");
                foreach (var p in items)
                {
                    var ppsqm = (double)(p.Price!.Value / p.Area!.Value);
                    sb.Append($"(P{properties.IndexOf(p) + 1},{ppsqm:F0}) ");
                }
                sb.AppendLine(@"};
\end{axis}
\end{tikzpicture}
\end{center}");
            }
        }

        // ── Chart 3: Surface area ──────────────────────────────────
        var withArea = properties.Where(p => p.Area.HasValue && p.Area > 0).ToList();
        if (withArea.Count >= 2)
        {
            var yMaxArea = Math.Ceiling((double)withArea.Max(p => p.Area!) * 1.15);
            sb.AppendLine($@"
\vspace{{0.5em}}
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{Superficie (m\textsuperscript{{2}})}}}},
    ybar,
    bar width=0.5cm,
    width=0.9\linewidth,
    height=8cm,
    ymin=0, ymax={yMaxArea.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)},
    symbolic x coords={{{string.Join(",", withArea.Select(p => $"P{properties.IndexOf(p) + 1}"))}}},
    xtick=data,
    x tick label style={{rotate=45, anchor=east, font=\small}},
    ylabel={{m\textsuperscript{{2}}}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small}},
    nodes near coords,
    nodes near coords style={{font=\tiny, rotate=90, anchor=west}},
    enlarge x limits=0.05,
    grid=major,
    grid style={{dashed, gray!30}},
]
\addplot[fill=usado!80] coordinates {{");
            foreach (var p in withArea)
                sb.Append($"(P{properties.IndexOf(p) + 1},{(double)p.Area!:F0}) ");
            sb.AppendLine(@"};
\end{axis}
\end{tikzpicture}
\end{center}");
        }

        // ── Chart 4: Bedrooms + Bathrooms grouped bar ──────────────
        var withRooms = properties.Where(p => p.Bedrooms.HasValue || p.Bathrooms.HasValue).ToList();
        if (withRooms.Count >= 2)
        {
            var maxRooms = Math.Max(
                withRooms.Max(p => p.Bedrooms ?? 0),
                withRooms.Max(p => p.Bathrooms ?? 0));

            sb.AppendLine($@"
\vspace{{0.5em}}
\begin{{center}}
\begin{{tikzpicture}}
\begin{{axis}}[
    title={{\textbf{{Dormitorios y Baños}}}},
    ybar,
    bar width=0.3cm,
    width=0.9\linewidth,
    height=7cm,
    ymin=0, ymax={maxRooms + 2},
    symbolic x coords={{{string.Join(",", withRooms.Select(p => $"P{properties.IndexOf(p) + 1}"))}}},
    xtick=data,
    x tick label style={{rotate=45, anchor=east, font=\small}},
    ylabel={{Cantidad}},
    ylabel style={{font=\small}},
    y tick label style={{font=\small}},
    nodes near coords,
    nodes near coords style={{font=\tiny}},
    enlarge x limits=0.05,
    legend style={{at={{(0.5,-0.2)}}, anchor=north, legend columns=2, font=\small}},
    grid=major,
    grid style={{dashed, gray!30}},
]
\addplot[fill=primary!70] coordinates {{");
            foreach (var p in withRooms)
                sb.Append($"(P{properties.IndexOf(p) + 1},{p.Bedrooms ?? 0}) ");
            sb.AppendLine(@"};
\addlegendentry{Dormitorios}");
            sb.Append(@"\addplot[fill=nuevo!70] coordinates {");
            foreach (var p in withRooms)
                sb.Append($"(P{properties.IndexOf(p) + 1},{p.Bathrooms ?? 0}) ");
            sb.AppendLine(@"};
\addlegendentry{Baños}
\end{axis}
\end{tikzpicture}
\end{center}");
        }

        sb.AppendLine(@"\newpage");

        // ── Price comparison summary ─────────────────────────────
        var priced = properties.Where(p => p.Price.HasValue && p.Price > 0).ToList();
        if (priced.Count >= 2)
        {
            sb.AppendLine(@"\section{Resumen de Precios}");

            // Group by currency
            var byCurrency = priced.GroupBy(p => (p.Currency ?? "CLP").ToUpper()).ToList();
            foreach (var group in byCurrency)
            {
                var prices = group.Select(p => (double)p.Price!.Value).ToList();
                var avg = prices.Average();
                var min = prices.Min();
                var max = prices.Max();
                var cheapest = group.OrderBy(p => p.Price).First();
                var expensive = group.OrderByDescending(p => p.Price).First();

                sb.AppendLine($@"
\subsection{{Propiedades en {group.Key}}}
\begin{{itemize}}[leftmargin=1.5em]
  \item Precio promedio: \textbf{{{FormatPrice(avg, group.Key)}}}
  \item Rango: {FormatPrice(min, group.Key)} -- {FormatPrice(max, group.Key)}
  \item Más económica: \textbf{{{Esc((cheapest.Title ?? "").Length > 40 ? cheapest.Title!.Substring(0, 40) + "..." : cheapest.Title)}}} ({FormatPrice((double)cheapest.Price!, group.Key)})
  \item Más cara: \textbf{{{Esc((expensive.Title ?? "").Length > 40 ? expensive.Title!.Substring(0, 40) + "..." : expensive.Title)}}} ({FormatPrice((double)expensive.Price!, group.Key)})
\end{{itemize}}");
            }
        }

        // ── Condition summary ────────────────────────────────────
        var withCondition = properties.Where(p => !string.IsNullOrEmpty(p.Condition)).ToList();
        if (withCondition.Count > 0)
        {
            var nuevo = withCondition.Count(p => p.Condition == "Nuevo");
            var usado = withCondition.Count(p => p.Condition == "Usado");
            sb.AppendLine($@"
\vspace{{0.5em}}
\subsection{{Estado de las propiedades}}
\begin{{itemize}}[leftmargin=1.5em]
  \item \textcolor{{nuevo}}{{Nuevas: {nuevo}}}
  \item \textcolor{{usado}}{{Usadas: {usado}}}
  \item Sin información: {properties.Count - withCondition.Count}
\end{{itemize}}");
        }

        // ── Footer ───────────────────────────────────────────────
        sb.AppendLine(@"
\vfill
\textcolor{muted}{\rule{\linewidth}{0.4pt}}\\
{\scriptsize\color{muted}
  Este informe comparativo fue generado automáticamente por \textbf{InmobiScrap}.
  Los datos provienen de scraping de portales inmobiliarios chilenos y pueden contener imprecisiones.
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