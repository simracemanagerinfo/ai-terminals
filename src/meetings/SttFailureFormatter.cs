using System.Text.Json;

namespace MeetingNotes;

/// <summary>
/// Presents safe diagnostic metadata emitted by MeetingTranscriber without
/// leaking raw stderr (which could include native runtime output or paths).
/// </summary>
public static class SttFailureFormatter
{
    public static string Explain(string stderr, int exitCode)
    {
        string? stage = null;
        string? type = null;

        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line.Trim());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("Code", out var code) ||
                    code.GetString() != "STT_FAILED")
                    continue;

                stage = root.TryGetProperty("Stage", out var st) ? st.GetString() : null;
                type = root.TryGetProperty("ErrorType", out var er) ? er.GetString() : null;
                break;
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }

        var safeStage = stage switch
        {
            "argomenti e configurazione" => "configurazione del modello e degli argomenti",
            "verifica dei file WAV" => "verifica dei file audio WAV",
            "caricamento modello Whisper" => "caricamento del modello Whisper",
            "conversione WAV MICROFONO" => "conversione del microfono",
            "conversione WAV AUDIO PC" => "conversione dell'audio del PC",
            "riconoscimento vocale MICROFONO" => "riconoscimento vocale del microfono",
            "riconoscimento vocale AUDIO PC" => "riconoscimento vocale dell'audio del PC",
            "verifica del risultato" => "verifica delle parole riconosciute",
            "scrittura TXT" => "salvataggio del file TXT",
            "inizializzazione" => "inizializzazione",
            _ => null
        };

        var detail = safeStage switch
        {
            "configurazione del modello e degli argomenti" =>
                "Controlla che il modello .bin esista e sia multilingua.",
            "verifica dei file audio WAV" =>
                "Controlla presenza, dimensione e accessibilità dei WAV.",
            "caricamento del modello Whisper" =>
                "Il modello potrebbe essere danneggiato o il runtime CPU incompatibile.",
            "conversione del microfono" or "conversione dell'audio del PC" =>
                "Il WAV potrebbe avere un formato, una durata o un'intestazione non valida.",
            "riconoscimento vocale del microfono" or "riconoscimento vocale dell'audio del PC" =>
                "Controlla compatibilità del runtime e qualità del WAV.",
            "verifica delle parole riconosciute" =>
                "Non sono state riconosciute parole. Controlla i WAV.",
            "salvataggio del file TXT" =>
                "Controlla lo spazio sul disco e i permessi della cartella trascrizioni.",
            _ => "Controlla i WAV e il modello Whisper."
        };

        var exitHex = unchecked((uint)exitCode).ToString("X8");
        // A native process can terminate before printing structured stderr.
        if (safeStage is null)
            return $"Il motore offline è terminato senza una diagnosi disponibile " +
                   $"(codice {exitCode}, 0x{exitHex}). {detail}";

        var kind = type switch
        {
            "FileNotFoundException" or "InvalidDataException" or
            "IOException" or "UnauthorizedAccessException" or
            "ArgumentException" or "InvalidOperationException" or
            "OutOfMemoryException" or "DllNotFoundException" or
            "BadImageFormatException" or "NotSupportedException" =>
                $" [{type}]",
            _ => ""
        };

        return $"Errore durante {safeStage}{kind}. {detail} " +
               $"Codice processo: {exitCode} (0x{exitHex}).";
    }
}
