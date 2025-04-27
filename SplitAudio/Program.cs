using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SplitAudio
{
    class Program
    {
        static string pattern;
        static TimeSpan segmentDuration;
        static string outputFolder;
        static bool logEnabled;
        static string logPath = "script_log.txt";

        static readonly List<string> SupportedExtensions = new List<string> { ".wav", ".mp3", ".flac", ".ogg", ".aac", ".m4a" };

        static void Main(string[] args)
        {
            try
            {
                ReadSettings();

                string ffmpegPath = ExtractFfmpeg();
                Directory.CreateDirectory(outputFolder);

                var audioFiles = Directory.GetFiles(Directory.GetCurrentDirectory())
                                          .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLower()))
                                          .ToArray();

                if (audioFiles.Length == 0)
                {
                    Console.WriteLine("Нет подходящих аудиофайлов для обработки.");
                    return;
                }

                Stream logStream = logEnabled
                    ? (Stream)new FileStream(logPath, FileMode.Append, FileAccess.Write)
                    : Stream.Null;

                using (var log = new StreamWriter(logStream))
                {
                    log.WriteLine($"=== Запуск: {DateTime.Now} ===");

                    foreach (var file in audioFiles)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);

                        if (!TryExtractDateFromName(fileName, pattern, out DateTime startTime, out string prefix))
                        {
                            log.WriteLine($"ПРОПУЩЕН: {fileName} — не удалось извлечь дату.");
                            continue;
                        }

                        log.WriteLine($"Обработка файла: {fileName}");
                        log.WriteLine($"Дата начала: {startTime:yyyy-MM-dd HH:mm:ss}");
                        log.WriteLine($"Префикс: {prefix}");

                        string tempDir = Path.Combine(Path.GetTempPath(), "segments_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);

                        string extension = Path.GetExtension(file).TrimStart('.').ToLower();
                        RunFfmpegSplit(ffmpegPath, file, segmentDuration, tempDir, extension);

                        var parts = Directory.GetFiles(tempDir)
                                              .Where(p => SupportedExtensions.Contains(Path.GetExtension(p).ToLower()))
                                              .OrderBy(f => f)
                                              .ToArray();
                        int count = 0;
                        int totalParts = parts.Length;

                        Console.WriteLine($"\n{fileName} — {totalParts} сегментов:");

                        foreach (var part in parts)
                        {
                            DateTime partTime = startTime.AddSeconds(segmentDuration.TotalSeconds * count);
                            string newName = $"{prefix}{partTime:yyyyMMddHHmmss}{Path.GetExtension(part)}";
                            string newPath = Path.Combine(outputFolder, newName);
                            File.Move(part, newPath);
                            log.WriteLine($"Создан: {newName}");
                            count++;

                            DrawProgressBar(count, totalParts);
                        }

                        Console.WriteLine($"\nГотово! Обработано {count} сегментов.\n");

                        Directory.Delete(tempDir, true);
                        log.WriteLine($"Готово: {fileName} → {count} сегментов\n");
                    }

                    log.WriteLine($"=== Завершено: {DateTime.Now} ===");
                }

                Console.WriteLine("Готово! Нажмите любую клавишу...");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ошибка: " + ex.Message);
            }
            finally
            {
                try
                {
                    string tempFfmpeg = Path.Combine(Path.GetTempPath(), "ffmpeg.exe");
                    if (File.Exists(tempFfmpeg)) File.Delete(tempFfmpeg);
                }
                catch { }

                Console.ReadKey();
            }
        }

        static void ReadSettings()
        {
            string settingsPath = "settings.txt";

            if (!File.Exists(settingsPath))
            {
                File.WriteAllLines(settingsPath, new[] {
                    "# Настройки программы нарезки аудиофайлов",
                    "# pattern: шаблон имени файла, где ? — игнорируемые символы",
                    "# ГГГГ — год, ММ — месяц, ДД — день, чч — часы, мм — минуты, сс — секунды",
                    "pattern=??ГГГГММДДччммсс",
                    "",
                    "# Длительность сегмента в формате ЧЧ:ММ:СС",
                    "segment_duration=02:30:00",
                    "",
                    "# Папка, куда будут сохраняться сегменты",
                    "output_folder=SOUND",
                    "",
                    "# Вести лог: true или false",
                    "log_enabled=true"
                });

                Console.WriteLine("Создан файл settings.txt с настройками по умолчанию. Отредактируйте его и перезапустите программу.");
                Environment.Exit(0);
            }

            var lines = File.ReadAllLines(settingsPath)
                            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"))
                            .ToList();

            foreach (var line in lines)
            {
                if (line.StartsWith("pattern="))
                    pattern = line.Substring(8).Trim();
                else if (line.StartsWith("segment_duration="))
                    segmentDuration = TimeSpan.Parse(line.Substring(17).Trim());
                else if (line.StartsWith("output_folder="))
                    outputFolder = line.Substring(14).Trim();
                else if (line.StartsWith("log_enabled="))
                    logEnabled = line.Substring(12).Trim().ToLower() == "true";
            }

            if (string.IsNullOrEmpty(pattern) || segmentDuration.TotalSeconds < 1 || string.IsNullOrEmpty(outputFolder))
                throw new Exception("Некорректный settings.txt.");
        }

        static bool TryExtractDateFromName(string fileName, string pattern, out DateTime date, out string prefix)
        {
            date = DateTime.MinValue;
            prefix = "";

            if (fileName.Length < pattern.Length)
                return false;

            int year = 0, month = 1, day = 1, hour = 0, minute = 0, second = 0;

            for (int i = 0; i < pattern.Length; i++)
            {
                string token = pattern.Substring(i, Math.Min(4, pattern.Length - i));

                if (token.StartsWith("?")) continue;

                if (token.StartsWith("ГГГГ"))
                {
                    year = int.Parse(fileName.Substring(i, 4));
                    i += 3;
                }
                else if (token.StartsWith("ММ"))
                {
                    month = int.Parse(fileName.Substring(i, 2));
                    i += 1;
                }
                else if (token.StartsWith("ДД"))
                {
                    day = int.Parse(fileName.Substring(i, 2));
                    i += 1;
                }
                else if (token.StartsWith("чч"))
                {
                    hour = int.Parse(fileName.Substring(i, 2));
                    i += 1;
                }
                else if (token.StartsWith("мм"))
                {
                    minute = int.Parse(fileName.Substring(i, 2));
                    i += 1;
                }
                else if (token.StartsWith("сс"))
                {
                    second = int.Parse(fileName.Substring(i, 2));
                    i += 1;
                }
            }

            try
            {
                date = new DateTime(year, month, day, hour, minute, second);
                prefix = fileName.Substring(0, pattern.IndexOf("ГГГГ"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void RunFfmpegSplit(string ffmpeg, string input, TimeSpan duration, string outputFolder, string extension)
        {
            var p = new Process();
            p.StartInfo.FileName = ffmpeg;
            p.StartInfo.Arguments = $"-i \"{input}\" -f segment -segment_time {duration.TotalSeconds} -c copy \"{Path.Combine(outputFolder, "part_%03d." + extension)}\"";
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.UseShellExecute = false;
            p.Start();
            p.WaitForExit();
        }

        static string ExtractFfmpeg()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "ffmpeg.exe");

            if (!File.Exists(tempPath))
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("ffmpeg.exe"));

                if (resourceName == null)
                    throw new Exception("ffmpeg.exe не найден в ресурсах.");

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    if (stream == null)
                        throw new Exception("Не удалось получить поток ресурса ffmpeg.exe.");

                    stream.CopyTo(file);
                }
            }

            return tempPath;
        }

        static void DrawProgressBar(int progress, int total)
        {
            int barWidth = 50;
            double percent = (double)progress / total;
            int completed = (int)(percent * barWidth);
            int remaining = barWidth - completed;
            int percentInt = (int)(percent * 100);

            Console.CursorVisible = false;
            Console.Write("\r[");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(new string('#', completed));

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(new string('-', remaining));

            Console.ResetColor();
            Console.Write($"] {progress}/{total} ({percentInt}%)");

            if (progress == total)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nГотово!");
                Console.ResetColor();
            }
        }
    }
}