using CommandLine.Text;
using CommandLine;
using Synthesia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Cli
{
   public class Program
   {

      public class Options
      {
         [Option('f', "folder", Required = false, HelpText = "Folder to read midi files from. Leave empty for current directory.")]
         public string Folder { get; set; }
      }

      public static void Main(string[] args)
      {
         var options = Parser.Default.ParseArguments<Options>(args).Value;

         var directoryInfo = new DirectoryInfo((options.Folder == null) ? Directory.GetCurrentDirectory() : options.Folder.Replace("\\", "/"));
         var files = directoryInfo.GetFiles().Where(x => new[] { ".mid", ".midi" }.Any(y => x.Name.EndsWith(y))).ToList();

         foreach (var file in files)
         {
            var songFile = new FileInfo(file.FullName);

            var songEntry = new SongEntry()
            {
               UniqueId = songFile.Md5sum(),
               Title = songFile.Name.Substring(0, songFile.Name.Length - songFile.Extension.Length)
            };

            Metadata.AddSong(songEntry);

            ImportFile();

            var outFile = new FileInfo(directoryInfo.FullName.Replace("\\", "/") + "/" + songEntry.Title + ".synthesia");
            using (var output = outFile.Create()) Metadata.Save(output);

            Metadata = new MetadataFile();
         }

         Console.ReadKey();
      }

      // Pretty much copy pasted code

      public static MetadataFile Metadata = new MetadataFile();

      struct ImportResults
      {
         public int Imported;
         public int Changed;
         public int Identical;

         public bool ProblemEncountered;

         public string ToDisplayString(string importType)
         {
            if (ProblemEncountered) return $"Unable to import {importType}.";

            if (Imported == 0) return "";

            return $"Imported {importType} for {Imported} song{(Imported == 1 ? "" : "s")}.  ({Changed} changed, {Identical} identical.)";
         }
      }

      private static void ImportFile()
      {
         // Based on GuiController.Import
         var options = ImportOptions.FingerHints | ImportOptions.HandParts | ImportOptions.Parts;

         bool standardPath = true;

         var results = new Dictionary<string, ImportResults>();
         if (options.HasFlag(ImportOptions.FingerHints)) results["finger hints"] = ImportFingerHints(standardPath);
         if (options.HasFlag(ImportOptions.HandParts)) results["hand parts"] = ImportHandParts(standardPath);
         if (options.HasFlag(ImportOptions.Parts)) results["parts"] = ImportParts(standardPath);

         var str = string.Join(", ", results.Where(x => x.Value.Imported == 1).Select(x => x.Key));

         Console.WriteLine(Metadata.Songs.First().Title + " - Imported: " + str);
      }

      private static ImportResults ImportFingerHints(bool standardPath)
      {
         var results = new ImportResults() { ProblemEncountered = true };
         string FingerHintPath = Path.Combine(GuiController.SynthesiaDataPath(standardPath), "fingers.xml");

         FileInfo fingerHintFile = new FileInfo(FingerHintPath);
         if (!fingerHintFile.Exists)
         {
            Console.WriteLine("Couldn't find finger hint file in the Synthesia data directory.  Aborting import.", "Missing fingers.xml");
            return results;
         }

         // Bulk pull the fingers out of the file
         Dictionary<string, string> allFingers = new Dictionary<string, string>();
         try
         {
            XDocument doc = XDocument.Load(FingerHintPath);
            XElement topLevel = doc.Element("LocalFingerInfoList") ?? throw new InvalidDataException("Couldn't find top-level LocalFingerInfoList element.");

            if (topLevel.AttributeOrDefault("version", "1") != "1")
            {
               Console.WriteLine("Data in fingers.xml is in a newer format.  Unable to import.  (Check for a newer version of the metadata editor.)", "Fingers.xml too new!");
               return results;
            }

            var elements = topLevel.Elements("FingerInfo");
            foreach (var fi in elements) allFingers[fi.AttributeOrDefault("hash")] = fi.AttributeOrDefault("fingers");
         }
         catch (Exception ex)
         {
            Console.WriteLine($"Unable to read fingers.xml.  Aborting import.\n\n{ex}", "Import error!");
            return results;
         }

         foreach (SongEntry s in Metadata.Songs.ToList())
         {
            if (!allFingers.ContainsKey(s.UniqueId)) continue;
            results.Imported++;

            string oldHints = s.FingerHints;
            string newHints = allFingers[s.UniqueId];

            if (oldHints == newHints) results.Identical++;
            else
            {
               s.FingerHints = newHints;
               Metadata.AddSong(s);

               results.Changed++;
            }
         }

         results.ProblemEncountered = false;
         return results;
      }

      private static ImportResults ImportHandParts(bool standardPath)
      {
         ImportResults results = new ImportResults() { ProblemEncountered = true };
         string SongInfoPath = Path.Combine(GuiController.SynthesiaDataPath(standardPath), "songInfo.xml");

         FileInfo songInfoFile = new FileInfo(SongInfoPath);
         if (!songInfoFile.Exists)
         {
            Console.WriteLine("Couldn't find song info file in the Synthesia data directory.  Aborting hand part import.", "Missing songInfo.xml");
            return results;
         }

         try
         {
            XDocument doc = XDocument.Load(SongInfoPath);
            XElement topLevel = doc.Element("LocalSongInfoList") ?? throw new InvalidDataException("Couldn't find top-level LocalSongInfoList element.");

            if (topLevel.AttributeOrDefault("version", "1") != "1")
            {
               Console.WriteLine("Data in songInfo.xml is in a newer format.  Unable to import hand parts.  (Check for a newer version of the metadata editor.)", "songInfo.xml too new!");
               return results;
            }

            var elements = topLevel.Elements("SongInfo");
            var parts = (from i in elements
                         select new
                         {
                            hash = i.AttributeOrDefault("hash"),
                            left = i.AttributeOrDefault("leftHand"),
                            right = i.AttributeOrDefault("rightHand"),
                            both = i.AttributeOrDefault("bothHands")
                         }).Where(i => !string.IsNullOrWhiteSpace(i.left) || !string.IsNullOrWhiteSpace(i.right) || !string.IsNullOrWhiteSpace(i.both)).ToDictionary(i => i.hash);

            foreach (var s in Metadata.Songs.ToList())
            {
               if (!parts.ContainsKey(s.UniqueId)) continue;
               results.Imported++;

               string oldParts = s.HandParts;

               var match = parts[s.UniqueId];
               string newParts = string.Join(";", match.left, match.right, match.both);

               if (oldParts == newParts) results.Identical++;
               else
               {
                  s.HandParts = newParts;
                  Metadata.AddSong(s);

                  results.Changed++;
               }
            }

         }
         catch (Exception ex)
         {
            Console.WriteLine($"Unable to read songInfo.xml.  Aborting hand part import.\n\n{ex}", "Import error!");
            return results;
         }

         results.ProblemEncountered = false;
         return results;
      }

      private static ImportResults ImportParts(bool standardPath)
      {
         ImportResults results = new ImportResults() { ProblemEncountered = true };
         string SongInfoPath = Path.Combine(GuiController.SynthesiaDataPath(standardPath), "songInfo.xml");

         FileInfo songInfoFile = new FileInfo(SongInfoPath);
         if (!songInfoFile.Exists)
         {
            Console.WriteLine("Couldn't find song info file in the Synthesia data directory.  Aborting part import.", "Missing songInfo.xml");
            return results;
         }

         try
         {
            XDocument doc = XDocument.Load(SongInfoPath);
            XElement topLevel = doc.Element("LocalSongInfoList") ?? throw new InvalidDataException("Couldn't find top-level LocalSongInfoList element.");

            if (topLevel.AttributeOrDefault("version", "1") != "1")
            {
               Console.WriteLine("Data in songInfo.xml is in a newer format.  Unable to import parts.  (Check for a newer version of the metadata editor.)", "songInfo.xml too new!");
               return results;
            }

            var elements = topLevel.Elements("SongInfo");
            var parts = (from i in elements
                         select new
                         {
                            hash = i.AttributeOrDefault("hash"),
                            parts = i.AttributeOrDefault("parts"),
                         }).Where(i => !string.IsNullOrWhiteSpace(i.parts)).ToDictionary(i => i.hash);

            foreach (var s in Metadata.Songs.ToList())
            {
               if (!parts.ContainsKey(s.UniqueId)) continue;
               results.Imported++;

               string oldParts = s.Parts;

               var match = parts[s.UniqueId];

               if (oldParts == match.parts) results.Identical++;
               else
               {
                  s.Parts = match.parts;
                  Metadata.AddSong(s);

                  results.Changed++;
               }
            }

         }
         catch (Exception ex)
         {
            Console.WriteLine($"Unable to read songInfo.xml.  Aborting part import.\n\n{ex}", "Import error!");
            return results;
         }

         results.ProblemEncountered = false;
         return results;
      }
   }
}
