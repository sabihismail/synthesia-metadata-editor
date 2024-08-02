using CommandLine;
using Synthesia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Text;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.MusicTheory;
using Melanchall.DryWetMidi.Common;

namespace Cli
{
   public class Program
   {
      public enum PartType
      {
         /// Ignore(-)
         Ignore = '-',

         /// Left(L)
         Left = 'L',

         /// Right(R)
         Right = 'R',

         /// Background(B)
         Background = 'B',

         /// Dispose(X)
         Dispose = 'X',
      }

      public class NotePart
      {
         public NotePart()
         {
            Length = 1;
         }

         public PartType PartType { get; set; }

         public int Length { get; set; }
      }

      public class Part : Dictionary<int, List<NotePart>>
      {
         public Part()
         {
         }

         public Part(PartType partType) : this()
         {
            AllPartType = partType;
         }

         public PartType AllPartType { get; set; }

         public PartType SearchPartType(int measure, int index)
         {
            if (this.ContainsKey(measure))
            {
               var sum = 0;
               return this[measure].FirstOrDefault(x =>
               {
                  sum += x.Length;
                  return sum > index;
               })?.PartType ?? AllPartType;
            }

            return AllPartType;
         }
      }

      public class Options
      {
         [Option('f', "folder", Required = false, HelpText = "Folder to read midi files from. Leave empty for current directory.")]
         public string Folder { get; set; }
      }

      public static IDictionary<int, IDictionary<int, string>> ConvertToTrackFormat(string formatString)
      {
         // Based on https://github.com/d5k-project/Synthesia.MetaDataParser/blob/master/Synthesia.MetaDataParser/SynthesiaMetaDataParser.cs
         if (string.IsNullOrEmpty(formatString))
         {
            return new Dictionary<int, IDictionary<int, string>>();
         }

         //get tracks
         var trackStrings = formatString.Replace(" ", string.Empty)
            .Split('t').Where(t => !string.IsNullOrEmpty(t))
            .ToDictionary(k => int.Parse(k.Split(':').FirstOrDefault()), v =>
            {
               var measure = ("t" + v).Split('m').Where(m => !string.IsNullOrEmpty(m) && m.Split(':').Count(x => !string.IsNullOrEmpty(x)) == 2)
                  .ToDictionary(k =>
                  {
                     var keyString = k.Split(':').FirstOrDefault();

                     if (keyString != null && keyString.Contains('t'))
                     {
                        return -1;
                     }

                     return int.Parse(keyString);
                  },
                  vv => vv.Split(':').LastOrDefault());

               return (IDictionary<int, string>)measure;
            });

         return trackStrings;
      }

      public static List<string> SpecialSplit(string input)
      {
         var result = new List<string>();

         var currentString = new StringBuilder(4);
         for (var i = 0; i < input.Length; i++)
         {
            var c = input[i];

            if (currentString.Length > 0)
            {
               // Determine whether we're at constraints or not.
               var firstCharLetter = currentString[0] >= 'A' && currentString[0] <= 'Z';
               var atMaxLetterLength = firstCharLetter && currentString.Length == 4;
               var atMaxNumberLength = !firstCharLetter && currentString.Length == 3;

               // Split if at max letter/number length, or if we're on a letter.
               var mustSplit = atMaxLetterLength || atMaxNumberLength || (c >= 'A' && c <= 'Z') || c == '-';

               if (mustSplit)
               {
                  // If we must split our string, then verify we're not leaving an orphaned '0'.
                  if (c == '0')
                  {
                     // Go back a letter, take it out of the new string, and set our `c` to it.
                     i--;
                     currentString.Length--;
                     c = input[i];
                  }

                  // Add and clear the string to our result.
                  result.Add(currentString.ToString());
                  currentString.Clear();
               }
            }

            // Add our `c` to the string.
            currentString.Append(c);
         }

         // Add our string to the result.
         result.Add(currentString.ToString());

         return result;
      }

      public static IDictionary<int, Part> ConvertStringToParts(string partsString)
      {
         if (string.IsNullOrEmpty(partsString))
         {
            return new Dictionary<int, Part>();
         }

         var dict = ConvertToTrackFormat(partsString);

         var trackType = dict.Values.First();

         var temp = dict.ToDictionary(k => k.Key,
             v =>
             {
                var part = new Part();

                var noteParts = v.Value;
                foreach (var notePart in noteParts)
                {
                   var notePartKey = notePart.Key;
                   var notePartValue = notePart.Value;

                   if (notePartKey == -1)
                   {
                      //Set all key
                      part.AllPartType = (PartType)notePartValue[0];
                   }
                   else
                   {
                      var notes = new List<NotePart>();
                      var notesStr = SpecialSplit(notePartValue);
                      foreach (var noteStr in notesStr)
                      {
                         var note = new NotePart
                         {
                            PartType = (PartType)noteStr[0]
                         };

                         if (noteStr.Any(char.IsDigit))
                            note.Length = int.Parse(noteStr.Substring(1, noteStr.Length - 1));

                         notes.Add(note);
                      }

                      //set partial key
                      part.Add(notePartKey, notes);
                   }
                }

                return part;
             });

         var asdf = temp.Values.First().Values.SelectMany(x => x).ToList();
         var test = new List<NotePart>();
         if (asdf != test)
         {

         }

         return temp;
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

            var parts = ConvertStringToParts(Metadata.Songs.First().Parts);
            if (parts.Count > 1)
            {
               Console.WriteLine("Yikes, expected 1 track");
            }

            var left = parts.First().Value.Select(x => x.Value).Select(x => x.Where(y => y.PartType == PartType.Left)).SelectMany(x => x).ToList();
            var right = parts.First().Value.Select(x => x.Value).Select(x => x.Where(y => y.PartType == PartType.Right)).SelectMany(x => x).ToList();

            var leftLength = left.Sum(y => y.Length);
            var rightLength = right.Sum(y => y.Length);

            var isLeft = leftLength > rightLength;

            var midiFile = MidiFile.Read(file.FullName);
            var notes = midiFile.GetTrackChunks().SelectMany(x => x.ManageNotes().Objects)
               .OrderBy(x => x.Time)
               .ToList()
               .Select((s, index) => new { s, index })
               .ToDictionary(x => x.index, x => x.s);

            

            var acceptibleTypes = new HashSet<MidiEventType>() { MidiEventType.NoteOff, MidiEventType.NoteOn };
            var events = midiFile.GetObjects(ObjectType.TimedEvent).Select(x => (TimedEvent)x).Where(x => !acceptibleTypes.Contains(x.Event.EventType)).ToList();

            var measureMapping = new Dictionary<long, List<Melanchall.DryWetMidi.Interaction.Note>>();
            using (var tempoMapManager = new TempoMapManager(midiFile.TimeDivision, midiFile.GetTrackChunks().Select(c => c.Events)))
            {
               var tempoMap123 = tempoMapManager.TempoMap;

               foreach (var note in notes)
               {
                  var bars = note.Value.TimeAs<BarBeatTicksTimeSpan>(tempoMap123);
                  var measure = bars.Bars + 1;

                  if (!lst.ContainsKey(measure))
                  {
                     measureMapping[measure] = new List<Melanchall.DryWetMidi.Interaction.Note>();
                  }

                  measureMapping[measure].Add(note.Value);
               }
            }

            var channelLeft = FourBitNumber.Parse("0");
            var channelRight = FourBitNumber.Parse("1");
            foreach (var note in notes)
            {
               note.Value.Channel = channelLeft;
            }

            var midiFileOut = new MidiFile
            {
               TimeDivision = midiFile.TimeDivision
            };

            var tempoMap = midiFileOut.GetTempoMap();

            var trackChunk = new TrackChunk();
            using (var notesManager = trackChunk.ManageNotes())
            {
               for (var i = 0; i < notes.Count(); i++)
               {
                  notesManager.Objects.Add(notes[i]);
               }
            }

            var trackChunkMeta = new TrackChunk();
            using (var eventsManager = trackChunkMeta.ManageTimedEvents())
            {
               foreach (var ev in events)
               {
                  eventsManager.Objects.Add(ev);
               }
            }

            midiFileOut.Chunks.Add(trackChunkMeta);
            midiFileOut.Chunks.Add(trackChunk);
            midiFileOut.Write(directoryInfo.FullName.Replace("\\", "/") + "/" + songEntry.Title + "z.mid");

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
