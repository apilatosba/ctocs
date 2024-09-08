using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// TODO: comments
//       anonymous unions and anonymous structs access syntax.
//       safe wrapper for pointer types
//       if a type starts with __builtin_ then it means it is not defined by the user but it is handled by the compiler.
//          if you see a type resolving to a __builtin_ then put this in your report i guess.
//       it looks like char* can be directly marshaled to string in some cases. i think it can be done in cases where the char* is unmodified by the function. needs more investigation tho
//       when i remove the enum prefix from members check if the rest of the identifier is a valid c# identifier. if not then dont delete the prefix i guess.
//       apparently function declarations in c allows function name to be enclosed with brackets. i should support this.
//       const struct members. try this if it is interop friendly then do it:
//          in c:
//             struct StructWithConstMember {
//                const int m;
//             };
//
//          in c#:
//             public unsafe partial struct ConstCStructMimic {
//                public int x {
//                   get;
//                   init;
//                }
//             }
//
//          if property (above approach) isnt interop friendly then the best solution i think is to create a readonly field and create a constructor for that readonly variable.
//       when i am writing global const variables if an enum value is set then csOutput doesnt compile because there is no implicit conversion from int to an user defined enum.
//          so a few options there are
//          in the structs dont use enum type but use int.
//          add an explicit cast. this option is not trivial
//          or kinda ignore since that case is not common. have two ways to do it. if there are no global const variables then dont do nothing.
//          or assume that it is fine and at the end after you create c# output create a dotnet project to test if it compiles if it doesnt compile then remove all the snus things from the code till it compiles.
//       bools and strings. Marshal.PtrToStringUTF8
//       create one to one constructor.
namespace Main {
   class Program {
      static void Main(string[] args) {
         if (args.Length == 0) {
            PrintHelpAndExit();
         }

         for (int i = 0; i < args.Length; i++) {
            if (args[i] == "--help" || args[i] == "-h") {
               PrintHelpAndExit();
            }
         }

         string soFile = "";
         Dictionary<string /*path*/, string /*content*/> headerFiles = new Dictionary<string, string>();
         Dictionary<string /*path*/, string /*content*/> preprocessedHeaderFiles = new Dictionary<string, string>();
         bool showProgress = true;

         for (int i = 0; i < args.Length; i++) {
            if (args[i] == "sofile") {
               if (i + 1 >= args.Length) {
                  Console.WriteLine("Missing .so file");
                  Environment.Exit(1);
               }

               soFile = args[i + 1];
               i++;
            } else if (args[i] == "hfiles") {
               if (i + 1 >= args.Length) {
                  Console.WriteLine("Missing .h files");
                  Environment.Exit(1);
               }
               
               for (i++; i < args.Length; i++) {
                  if (args[i] == ",,") {
                     break;
                  } else if (args[i].EndsWith(",,")) {
                     headerFiles.TryAdd(args[i].Trim().Substring(0, args[i].Length - 2), "");
                     break;
                  } else {
                     headerFiles.TryAdd(args[i].Trim(), "");
                  }
               }
            } else if (args[i] == "phfiles") {
               if (i + 1 >= args.Length) {
                  Console.WriteLine("Missing .h files");
                  Environment.Exit(1);
               }
               
               for (i++; i < args.Length; i++) {
                  if (args[i] == ",,") {
                     break;
                  } else if (args[i].EndsWith(",,")) {
                     preprocessedHeaderFiles.TryAdd(args[i].Trim().Substring(0, args[i].Length - 2), "");
                     break;
                  } else {
                     preprocessedHeaderFiles.TryAdd(args[i].Trim(), "");
                  }
               }
            } else if(args[i] == "--no-show-progress") {
               showProgress = false;
            } else {
               Console.WriteLine($"Unknown argument: {args[i]} at location {i}");
               Environment.Exit(1);
            }
         }

         if (soFile == "") {
            Console.WriteLine("Missing .so file");
            Environment.Exit(1);
         }

         if (headerFiles.Count == 0) {
            Console.WriteLine("Missing .h files");
            Environment.Exit(1);
         }

         if (preprocessedHeaderFiles.Count == 0) {
            Console.WriteLine("Missing preprocessed .h files");
            Environment.Exit(1);
         }

         // Do given files exists?
         {
            if (!File.Exists(soFile)) {
               Console.WriteLine($"File {soFile} does not exist");
               Environment.Exit(1);
            }

            foreach (string headerFile in headerFiles.Keys) {
               if (!File.Exists(headerFile)) {
                  Console.WriteLine($"File {headerFile} does not exist");
                  Environment.Exit(1);
               }
            }

            foreach (string preprocessedHeaderFile in preprocessedHeaderFiles.Keys) {
               if (!File.Exists(preprocessedHeaderFile)) {
                  Console.WriteLine($"File {preprocessedHeaderFile} does not exist");
                  Environment.Exit(1);
               }
            }
         }

         List<DynsymTableEntry> dynsymTable = new List<DynsymTableEntry>();
         HashSet<string> exposedFunctionsInSOFile;
         // Read .so file into dynsymTable
         {
            if (showProgress) {
               Console.WriteLine("Reading .so file...");
            }

            Process readelfProcess = new Process();
            ProcessStartInfo readelfProcessStartInfo = new ProcessStartInfo("readelf", $"-W --dyn-syms \"{soFile}\"") {
               RedirectStandardOutput = true,
            };

            readelfProcess.StartInfo = readelfProcessStartInfo;
            readelfProcess.Start();
            StringBuilder readelfOutputBuilder = new StringBuilder();
            string readelfOutput;
            {
               StreamReader readelfProcessOutputReader = readelfProcess.StandardOutput;
               string buffer;
               do {
                  buffer = readelfProcessOutputReader.ReadToEnd();
                  readelfOutputBuilder.Append(buffer);
               } while (buffer != "");
               readelfProcessOutputReader.Close();
            }
            readelfProcess.WaitForExit(); // if standard output is redirected then WaitForExit must be called after reading the output otherwise it hangs forever
            readelfOutput = readelfOutputBuilder.ToString();

            Regex tableEntryRegex = new Regex(@"\s+(?<num>\d+):\s+(?<value>[0-9a-fA-F]+)\s+(?<size>\d+)\s+(?<type>\S+)\s+(?<bind>\S+)\s+(?<vis>\S+)\s+(?<ndx>\S+)\s+(?<name>\S+)");
            MatchCollection matches = tableEntryRegex.Matches(readelfOutput);

            for (int i = 0; i < matches.Count; i++) {
               Match match = matches[i];
               DynsymTableEntry dynsymTableEntry = new DynsymTableEntry {
                  num = uint.Parse(match.Groups["num"].Value),
                  value = ulong.Parse(match.Groups["value"].Value, System.Globalization.NumberStyles.HexNumber),
                  size = uint.Parse(match.Groups["size"].Value),
                  type = match.Groups["type"].Value,
                  bind = match.Groups["bind"].Value,
                  vis = match.Groups["vis"].Value,
                  ndx = match.Groups["ndx"].Value,
                  name = match.Groups["name"].Value,
               };
               dynsymTable.Add(dynsymTableEntry);
            }

            exposedFunctionsInSOFile = ExtractOutExposedFunctionsFromDynsymTable(dynsymTable);
         }

         // Read .h files
         {
            if (showProgress) {
               Console.WriteLine("Reading .h files...");
            }

            foreach (string headerFile in headerFiles.Keys) {
               if (showProgress) {
                  Console.WriteLine($"\tReading {headerFile}...");
               }

               headerFiles[headerFile] = File.ReadAllText(headerFile);
            }
         }

         // Read preprocessed .h files
         {
            Regex gibberishLineGeneratedByPreprocessorRegex = new Regex(@"^# \d+ "".*"".*$", RegexOptions.Multiline);
            if (showProgress) {
               Console.WriteLine("Reading preprocessed .h files...");
            }

            foreach (string preprocessedHeaderFile in preprocessedHeaderFiles.Keys) {
               if (showProgress) {
                  Console.WriteLine($"\tReading {preprocessedHeaderFile}...");
               }
               
               preprocessedHeaderFiles[preprocessedHeaderFile] = File.ReadAllText(preprocessedHeaderFile);

               // remove gibberish lines
               {
                  for (; ; ) {
                     Match gibberishLineMatch = gibberishLineGeneratedByPreprocessorRegex.Match(preprocessedHeaderFiles[preprocessedHeaderFile]);
                     if (!gibberishLineMatch.Success) {
                        break;
                     }
                     preprocessedHeaderFiles[preprocessedHeaderFile] = preprocessedHeaderFiles[preprocessedHeaderFile].Remove(gibberishLineMatch.Index, gibberishLineMatch.Length);
                  }
               }
            }
         }

         Report report = new Report();
         Dictionary<string, Type> singleLineDefineTypes = new Dictionary<string, Type>(); // includes the type of the entry in singleLineDefines if it is written to csOutput. #define FOO 5 -> singleLineDefineTypes["FOO"] = typeof(int)
         Dictionary<string, string> singleLineDefines = new Dictionary<string, string>(); // only the ones that can be written as "const ... = ..." are added here. #define FOO 5 -> defines["FOO"] = "5"
         Dictionary<string, string> defines = new Dictionary<string, string>();
         HashSet<string> definesThatAreDefinedMoreThanOnce = new HashSet<string>();
         {
            // TODO: single line define should also support this kinda thing. #define FOO 1 << 8. #define FOO (1 << 8)
            Regex singleLineDefineRegex = new Regex(@"[ \t]*#[ \t]*define[ \t]+(?<name>\w+)[ \t]+(?<value>[""']?[\w. \t\-]+[""']?)[ \t]*");
            Regex anyDefineRegex = new Regex(@"# *define(?:\\\r?\n)?[ \t]+(?:\\\r?\n)?[ \t]*(?<name>\w+)(?:\\\r?\n)?[ \t]+(?:\\\r?\n)?[ \t]*(?<value>(?:[\w""' \t{}();,+\-*/=&%<>|.!#\^$?:]|\\\r?\n)+)\r?\n"); // macros with arguments are not supported
            foreach (var kvp in headerFiles) {
               string file = kvp.Value; // file contents
               string path = kvp.Key;
               if (showProgress) {
                  Console.WriteLine($"Processing defines [{path}]...");
               }
               
               // single line defines
               {
                  MatchCollection matches = singleLineDefineRegex.Matches(file);
                  foreach (Match match in matches) {
                     string name = match.Groups["name"].Value;
                     string value = match.Groups["value"].Value.Trim();
                     name = EnsureVariableIsNotAReservedKeyword(name);

                     if (value != string.Empty && !singleLineDefines.TryAdd(name, value)) {
                        definesThatAreDefinedMoreThanOnce.Add(name);
                        // Console.WriteLine($"Warning: defines dictionary already includes \"{name}\". Value: \"{singleLineDefines[name]}\". you tried to set it to \"{value}\"");
                     }
                  }
               }

               // any define
               {
                  MatchCollection matches = anyDefineRegex.Matches(file);
                  foreach (Match match in matches) {
                     string name = match.Groups["name"].Value;
                     string value = match.Groups["value"].Value;
                     name = EnsureVariableIsNotAReservedKeyword(name);

                     if (!defines.TryAdd(name, value)) {
                        definesThatAreDefinedMoreThanOnce.Add(name);
                     }
                  }
               }
            }

            report.definesThatAreDefinedMoreThanOnce = definesThatAreDefinedMoreThanOnce;

            // remove defines that are defined more than once
            foreach (string define in definesThatAreDefinedMoreThanOnce) {
               defines.Remove(define);
               singleLineDefines.Remove(define);
            }
         }

         // used for generating unique names for anonymous structs that has no variable declaration. both for anonymous structs and thier generated variables. __ANONYMOUS__<iota>, __ANONYMOUS__<surroundingStructName>_<iota>_struct
         Iota iota = new Iota();
         Regex openSquareBracketRegex = new Regex(@"\[");
         Regex openCurlyBraceRegex = new Regex(@"\{");
         Regex noNewBeforeCurlyBraceRegex = new Regex(@"(?<!new\(\)\s*)\{"); // if "{" preceded by new() then dont match it.
         Regex dotMemberEqualsRegex = new Regex(@"[{\s,](?<dot>\.)\s*\w+\s*="); // used to remove dots from global const variables

         HashSet<string> opaqueStructs = new HashSet<string>();
         HashSet<string> opaqueUnions = new HashSet<string>();
         HashSet<string> opaqueEnums = new HashSet<string>();
         HashSet<string /*union name*/> unionsThatArePresentInOriginalCCode = new HashSet<string>();
         HashSet<string /*struct name*/> structsThatArePresentInOriginalCCode = new HashSet<string>(); // only the structs that are present in the original C code. not the ones that are generated by this program.
         Dictionary<string /*global const variable name*/, GlobalConstVariableData> globalConstVariableDatas = new Dictionary<string, GlobalConstVariableData>();
         Dictionary<string /*typedef function pointer name*/, FunctionPointerData> functionPointerDatas = new Dictionary<string, FunctionPointerData>();
         Dictionary<string /*function name*/, FunctionData> functionDatas = new Dictionary<string, FunctionData>();
         Dictionary<string /*struct name*/, StructData> structDatas = new Dictionary<string, StructData>(); // contains all the structs that need to processed and need to be written to csOutput. including the ones that are generated by this program
         // uses StructData because i didnt want to rename it to StructOrUnionData.
         Dictionary<string /*union name*/, StructData> unionDatas = new Dictionary<string, StructData>(); // contains all the unions that need to processed and need to be written to csOutput. including the ones that are generated by this program
         // basically keys and values are swapped compared to typedef syntax. typedef int newType; -> typedefs["newType"] = "int"
         // typedef struct Bullet Bullet -> typedefs["Bullet"] = "struct Bullet"
         Dictionary<string /*new type*/, string /*what new type defined as*/> typedefs = new Dictionary<string, string>();
         Dictionary<string /*enum name*/, EnumData> enumDatas = new Dictionary<string, EnumData>();
         // i use this to write the enum members as "const int ... = ..." because syntax wise thats what equals to c. but doing it this way (by storing them in a seperate collection) is no good. there are lots multiple data around here
         //    instead what i should do is to defer the removal of prefix and thats it.
         Dictionary<string /*enum name*/, EnumData> enumDatasButPrefixesAreNotRemoved = new Dictionary<string, EnumData>();
         List<List<EnumMember>> anonymousEnums = new List<List<EnumMember>>();
         {
            // TODO: handle typedef. if there is typedef keyword the ending has to be with semicolon { not allowed
            //       what is this? i forgor. what is typedefd function? omegaluliguess
            //       maybe dont allow typedef here. and create another regex for typedefd functions. typedefd functions i think used like function pointers in c.
            // NOTE: return type might match typedef. if return type contains typedef skip it
            Regex functionRegex = new Regex(@"(?<!typedef\s+)(?<returnType>\w+[\w\s]*?[*\s]+?)\s*(?<functionName>\w+)\s*\((?<args>[\w,\s*()\[\]]*?)\s*(?<variadicPart>\.\.\.)?\s*\)\s*[{;]", RegexOptions.Singleline | RegexOptions.Multiline); // looks for a declaration or implementation. ending might be "}" or ";"
            Regex functionArgRegex = new Regex(@"(?<type>\w+[\w\s]*?[*\s]*?)\s*(?:(?<=[\s*])(?<parameterName>\w+))?\s*,"); // parameter name optional.
            Regex functionArgArrayRegex = new Regex(@"(?<type>\w+[\w\s]*?[*\s]*?)\s*(?:(?<=[\s*])(?<parameterName>\w+))?\s*(?<arrayPart>\[[\w\[\]\s+\-*/^%&()|~]*?\])\s*,"); // before applying this regex apply RemoveConsts() function consider this: const char* const items[MAX + MIN]. there is a star between two const keywords
            // allow zero stars
            Regex functionArgFunctionPointerRegex = new Regex(@"(?<returnType>\w+[\w\s]*?[*\s]+?)\s*\(\s*(?<stars>[*\s]*?)\s*(?<parameterName>\w+)?(?:\s*(?<arrayPart>\[[\w\[\]\s+\-*/^%&()|~]*?\]))?\s*\)\s*\((?<args>[\w,\s*()\[\]]*?)\s*(?<variadicPart>\.\.\.)?\s*\)\s*,"); // expects a comma at the end just like functionArgRegex does
            Regex structRegex = new Regex(@"struct\s+(?<name>\w+)\s*\{(?<fields>.*?)\}\s*(?<variableName>\w+)?\s*;", RegexOptions.Singleline | RegexOptions.Multiline); // this regex stops early if struct contains complex members. structs inside of structs or unions.
            Regex greedyStructRegex = new Regex(@"struct\s+(?<name>\w+)\s*\{(?<fields>.*)\}\s*(?<variableName>\w+)?\s*;", RegexOptions.Singleline | RegexOptions.Multiline);
            Regex typedefStructRegex = new Regex(@"typedef\s+struct(?:\s+(?<name>\w+))?\s*\{(?<fields>.*?)\}\s*(?<typedefdName>\w+)\s*;", RegexOptions.Singleline | RegexOptions.Multiline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyTypedefStructRegex = new Regex(@"typedef\s+struct(?:\s+(?<name>\w+))?\s*\{(?<fields>.*)\}\s*(?<typedefdName>\w+)\s*;", RegexOptions.Singleline | RegexOptions.Multiline);
            Regex structMemberRegex = new Regex(@"(?<type>\w+[\w\s]*?[*\s]+?)\s*(?<name>\w+)\s*(?:\s*:\s*(?<bitfieldValue>\w+))?\s*;"); // functionArgRegex vs this. "type" has to end with a star or white space here. name is not optional
            Regex anonymousStructRegex = new Regex(@"struct\s*\{(?<fields>.*?)\}\s*;", RegexOptions.Singleline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyAnonymousStructRegex = new Regex(@"struct\s*\{(?<fields>.*)\}\s*;", RegexOptions.Singleline);
            Regex anonymousUnionRegex = new Regex(@"union\s*\{(?<fields>.*?)\}\s*;", RegexOptions.Singleline); // this regex stops early if union contains complex members. unions inside of unions or structs. use GetWholeStruct() function
            Regex greedyAnonymousUnionRegex = new Regex(@"union\s*\{(?<fields>.*)\}\s*;", RegexOptions.Singleline);
            Regex anonymousStructWithVariableDeclarationRegex = new Regex(@"struct\s*\{(?<fields>.*?)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyAnonymousStructWithVariableDeclarationRegex = new Regex(@"struct\s*\{(?<fields>.*)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline);
            Regex anonymousUnionWithVariableDeclarationRegex = new Regex(@"union\s*\{(?<fields>.*?)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyAnonymousUnionWithVariableDeclarationRegex = new Regex(@"union\s*\{(?<fields>.*)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline);
            Regex typedefRegex = new Regex(@"typedef\s+(?<originalType>\w+[\w\s]*?)\s+(?<newType>\w+)\s*;");
            Regex structMemberArrayRegex = new Regex(@"(?<type>\w+[\w\s]*?[*\s]+?)\s*(?<name>\w+)\s*(?<size>\[[\w\[\]\s+\-*/^%&()|~]+?\])\s*;"); // size contains everything between the brackets and the brackets. int foo[5][7] -> size = "[5][7]"
            Regex unionRegex = new Regex(@"union\s+(?<name>\w+)\s*\{(?<fields>.*?)\}\s*(?<variableName>\w+)?\s*;", RegexOptions.Singleline); // this regex stops early if union contains complex members. unions inside of unions or structs.
            Regex greedyUnionRegex = new Regex(@"union\s+(?<name>\w+)\s*\{(?<fields>.*)\}\s*(?<variableName>\w+)?\s*;", RegexOptions.Singleline);
            Regex typedefUnionRegex = new Regex(@"typedef\s+union(?:\s+(?<name>\w+))?\s*\{(?<fields>.*?)\}\s*(?<typedefdName>\w+)\s*;", RegexOptions.Singleline);
            Regex greedyTypedefUnionRegex = new Regex(@"typedef\s+union(?:\s+(?<name>\w+))?\s*\{(?<fields>.*)\}\s*(?<typedefdName>\w+)\s*;", RegexOptions.Singleline);
            Regex enumRegex = new Regex(@"enum\s+(?<name>\w+)\s*\{(?<members>.*?)\}\s*(?<variableName>\w+)?\s*;", RegexOptions.Singleline);
            Regex typedefEnumRegex = new Regex(@"typedef\s+enum(?:\s+(?<name>\w+))?\s*\{(?<members>.*?)\}\s*(?<typedefdName>\w+)\s*;", RegexOptions.Singleline);
            Regex enumMemberRegex = new Regex(@"(?<identifier>\w+)(?:\s*=\s*(?<value>[\w\s'()+\-*\/&|%<>!\^~]+?))?\s*,"); // regex reqiures a comma at the end. i will manually add a comma to the end of the membersString so it doesnt miss the last element
            Regex anonymousEnumRegex = new Regex(@"enum\s*\{(?<members>.*?)\}\s*;", RegexOptions.Singleline);
            Regex anonymousEnumWithVariableDeclarationRegex = new Regex(@"(?<!typedef\s+)enum\s*\{(?<members>.*?)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline); // do not match if starts with typedef
            // TODO: return type function pointer should be handled seperately.
            Regex typedefFunctionPointerRegex = new Regex(@"typedef\s+(?<returnType>\w+[\w\s]*?[*\s]+?)\s*\(\s*(?<stars>\*[*\s]*?)\s*(?<name>\w+)(?:\s*(?<arrayPart>\[[\w\[\]\s+\-*/^%&()|~]*?\]))?\s*\)\s*\((?<args>[\w,\s*()\[\]]*?)\s*(?<variadicPart>\.\.\.)?\s*\)\s*;");
            // apparently you can put the keywords in any order. const int. int const. const static int.
            // i only support the ones that start with const keyword and between const and type these words are okay (static|volatile|register).
            Regex constVariableRegex = new Regex(@"const\s+(?:\s*(?:static|volatile|register)\s+)?(?<type>\w+[\w\s]*?[*\s]*?)\s*(?<name>\w+)(?:\s*(?<arrayPart>\[[\w\[\]\s+\-*/^%&()|~]*?\]))?\s*=\s*(?<value>.+?);", RegexOptions.Singleline); // value being .+? is no problem since the ending is semicolon and value cant contain semicolon
            // i did a little hack in the arrayPart. it was optional at first but regex has to capture it so i know what variable group it belongs to. if regex doesnt capture it indices messes up and i no further know what capture belongs to what.
            //    so it is required but it may match empty string which is not an array at all. so you need to check if arrayPart is empty to find out if it actually matched
            Regex variableDeclarationWithCommaOperator = new Regex(@"(?<type>\w+[\w\s]*?)\s*(?<variable>(?<stars>[\s*]*?)(?<=[\s*,])(?<variableName>\w+)\s*(?<arrayPart>(?(?=\[)(\[[\w\[\]\s+\-*/^%&()|~]+?\])|()))\s*,)+(?<lastVariable>(?<lastVariableStars>[\s*]*?)(?<=[\s*,])(?<lastVariableVariableName>\w+)\s*(?<lastVariableArrayPart>(?(?=\[)(\[[\w\[\]\s+\-*/^%&()|~]+?\])|())))\s*;"); // at least one comma operated value is required
            // NOTE: those matches may match transparent types. so in order to understand if it is opaque or not check the structDatas dictionary if the struct name is present there.
            Regex opaqueStructRegex = new Regex(@"struct\s+(?<name>\w+)\s*;");
            // i would make the regex like this typedef\s+struct\s+(?<name>\w+)(\s+\w+)?\s*; notice the optional typedefdName but i didnt since the opaqueStructRegex already detects such situations
            Regex typedefOpaqueStructRegex = new Regex(@"typedef\s+struct\s+(?<name>\w+)\s+\w+\s*;");
            Regex opaqueUnionRegex = new Regex(@"union\s+(?<name>\w+)\s*;");
            Regex typedefOpaqueUnionRegex = new Regex(@"typedef\s+union\s+(?<name>\w+)\s+\w+\s*;");
            Regex opaqueEnumRegex = new Regex(@"union\s+(?<name>\w+)\s*;");
            Regex typedefOpaqueEnumRegex = new Regex(@"typedef\s+enum\s+(?<name>\w+)\s+\w+\s*;");
            foreach (var kvp in preprocessedHeaderFiles) {
               string file = kvp.Value; // file contents
               string path = kvp.Key;
               
               Queue<(string name, string fields)> unionFrontier = new Queue<(string name, string fields)>();
               Queue<(string name, string fields)> structFrontier = new Queue<(string name, string fields)>();
               Queue<(string enumName, string members)> enumFrontier = new Queue<(string enumName, string members)>();
               Queue<(string name, string returnType, string args, string stars, Group arrayPart, Group variadicPart, string surroundingFunctionName /*null if there isnt any*/)> functionPointerFrontier = new Queue<(string name, string returnType, string args, string stars, Group arrayPart, Group variadicPart, string surroundingFunctionName)>();

               // MODIFYING THE FILE STRING but not modifying the value inside the preprocessedHeaderFiles dictionary. surely this is not gonna break anything Clueless
               {
                  // comma operator. converting the normal declaration form.
                  // NOTE: since this part modifies the "file" string which is usually a pretty big string, modifying it is costly.
                  //       i can move this part to smaller parts like only apply this to fields of a struct. that way it is a lot a lot faster but then i miss const global variables.
                  //       maybe i should create another regex for global const variables with comma operator and move this part to fields of structs and unions.
                  //       while processing raylib 12 iterations of this for loop takes about idk 6 seconds or something which is a lot.
                  // TODO: you dummy dum dumm. if declare consts you also have to provide value to them so this regex wonth even match global const variables.
                  //       move this to struct fields and create abother regex for global const variables.
                  {
                     if (showProgress) {
                        Console.WriteLine($"Processing comma operator [{path}]...");
                     }

                     for (; ; ) {
                        Match match = variableDeclarationWithCommaOperator.Match(file);
                        if (!match.Success) {
                           break;
                        }

                        string type = match.Groups["type"].Value;
                        int totalVariableCount = match.Groups["variable"].Captures.Count + 1; // +1 for the last variable
                        Debug.Assert(totalVariableCount >= 2);
                        
                        string[] starsArray = new string[totalVariableCount];
                        string[] variableNameArray = new string[totalVariableCount];
                        string[] arrayPartArray = new string[totalVariableCount];
                        {
                           for (int i = 0; i < totalVariableCount - 1; i++) {
                              starsArray[i] = match.Groups["stars"].Captures[i].Value;
                              variableNameArray[i] = match.Groups["variableName"].Captures[i].Value;
                              arrayPartArray[i] = match.Groups["arrayPart"].Captures[i].Value;
                           }

                           starsArray[totalVariableCount - 1] = match.Groups["lastVariableStars"].Value;
                           variableNameArray[totalVariableCount - 1] = match.Groups["lastVariableVariableName"].Value;
                           arrayPartArray[totalVariableCount - 1] = match.Groups["lastVariableArrayPart"].Value;
                        }

                        file = file.Remove(match.Index, match.Length)
                                 .Insert(match.Index, GetSingleVariableDeclarationsFromCommaOperatorDeclaredVariablesStillInC(type, starsArray, variableNameArray, arrayPartArray));
                     }
                  }

                  // pretending that in the original c code there is no csharp only keywords.
                  // e.g. allow int to stay as int but dont allow uint to stay as uint
                  //    typedef char uint;
                  //
                  //    in this case assume that uint wasnt written but uint_ was written instead
                  //
                  //    typedef char uint_;
                  {
                     if (showProgress) {
                        Console.WriteLine($"Processing csharp only keywords [{path}]...");
                     }
                     
                     HashSet<string> csharpOnlyKeywordsThatAreTypes = new HashSet<string>(TypeInfo.csharpReservedKeywordsThatAreTypes);
                     csharpOnlyKeywordsThatAreTypes.ExceptWith(TypeInfo.cReservedKeywordsThatAreTypes);

                     foreach (string keyword in csharpOnlyKeywordsThatAreTypes) {
                        Regex keywordRegex = new Regex(@$"\b{keyword}\b");
                        keywordRegex.Replace(file, $"{keyword}_");
                     }
                  }
               }

               // typedefs
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing typedefs [{path}]...");
                  }

                  MatchCollection matches = typedefRegex.Matches(file);
                  foreach (Match match in matches) {
                     string originalType = match.Groups["originalType"].Value;
                     string newType = match.Groups["newType"].Value;
                     originalType = originalType.Trim();
                     // originalType = EnsureTypeIsNotAReservedKeyword(originalType);
                     // newType = EnsureTypeIsNotAReservedKeyword(newType);

                     // NOTE: redefinition is allowed if the new type is the same as the old type
                     {
                        // typedef struct Bullet Bullet;
                        // struct Bullet {
                        //   ...
                        // };

                        // typedef Bullet Bullet; // OKAY
                        // typedef char* Bullet;  // NOT OKAY
                     }

                     if (newType != originalType) { // dont allow self cycles otherwise resolving typedefs will result in an infinite loop. e.g. typedef int newType; typedef newType newType;
                        if (!typedefs.TryAdd(newType, originalType)) {
                           // this cant happen in a valid c code. so not even putting it to the report
                           // Console.WriteLine($"Warning: typedefs dictionary already includes \"{newType}\". Value: \"{typedefs[newType]}\". you tried to set it to \"{originalType}\"");
                        }
                     }
                  }
               }

               // global const variables
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing global const variables [{path}]...");
                  }

                  MatchCollection matches = constVariableRegex.Matches(file);
                  foreach (Match match in matches) {
                     string type = match.Groups["type"].Value.Trim();
                     string name = match.Groups["name"].Value;
                     Group arrayPartGroup = match.Groups["arrayPart"];
                     string value = match.Groups["value"].Value.Trim();
                     
                     name = EnsureVariableIsNotAReservedKeyword(name);
                     // type = EnsureTypeIsNotAReservedKeyword(type);

                     globalConstVariableDatas.TryAdd(name, new GlobalConstVariableData() {
                        name = name,
                        type = type,
                        value = value,
                        arrayPart = arrayPartGroup.Success ? arrayPartGroup.Value : null
                     });
                  }
               }

               // functions
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing functions [{path}]...");
                  }

                  MatchCollection matches = functionRegex.Matches(file);
                  foreach (Match functionMatch in matches) {
                     string returnType = functionMatch.Groups["returnType"].Value.Trim();
                     string functionName = functionMatch.Groups["functionName"].Value.Trim();
                     string functionArgs = functionMatch.Groups["args"].Value.Trim();
                     bool isVariadic = functionMatch.Groups["variadicPart"].Success;                     
                     
                     if (returnType.Contains("typedef")) {
                        report.typedefdFunctionsOrWronglyCapturedFunctionPointers.Add(functionName); // TODO: those function should be added to delegates?
                        continue;
                     }

                     if (!exposedFunctionsInSOFile.Contains(functionName)) {
                        report.functionsThatExistInHeaderFileButNotExposedInSOFile.Add(functionName);
                        continue;
                     }

                     // this is necessary since functionArgRegex matches void
                     if (functionArgs == "void") {
                        functionArgs = "";
                     } else {
                        functionArgs += ','; // add a comma to the end so the last argument can be processed. functionArgRegex requires a comma at the end.
                        functionArgs = RemoveConsts(functionArgs, out _);
                     }
                     returnType = RemoveConsts(returnType, out _).Trim();

                     List<IFunctionParameterData> parameters = ExtractOutParameterDatasAndResolveFunctionPointers(
                        functionArgs,
                        functionName,
                        functionArgRegex,
                        functionArgArrayRegex,
                        functionArgFunctionPointerRegex,
                        functionPointerFrontier,
                        iota
                     );

                     functionDatas.TryAdd(functionName, new FunctionData() {
                        returnType = returnType,
                        name = functionName,
                        parameters = parameters,
                        isVariadic = isVariadic
                     });
                  }
               }

               // function pointers
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing function pointers [{path}]...");
                  }

                  MatchCollection matches = typedefFunctionPointerRegex.Matches(file);
                  foreach (Match match in matches) {
                     string returnType = match.Groups["returnType"].Value.Trim();
                     string stars = match.Groups["stars"].Value.Trim();
                     string name = match.Groups["name"].Value;
                     Group arrayPart = match.Groups["arrayPart"];
                     string functionArgs = match.Groups["args"].Value.Trim();
                     Group variadicPart = match.Groups["variadicPart"];

                     functionPointerFrontier.Enqueue((name, returnType, functionArgs, stars, arrayPart, variadicPart, null));
                  }

                  while (functionPointerFrontier.Count > 0) {
                     (string name, string returnType, string functionArgs, string stars, Group arrayPart, Group variadicPart, string surroundingFunctionName) = functionPointerFrontier.Dequeue();

                     // this is not necessary since functionArgFunctionPointerRegex doesnt match void
                     if (functionArgs == "void") {
                        functionArgs = "";
                     } else {
                        functionArgs += ',';
                        functionArgs = RemoveConsts(functionArgs, out _);
                     }
                     returnType = RemoveConsts(returnType, out _).Trim();

                     List<IFunctionParameterData> parameters = ExtractOutParameterDatasAndResolveFunctionPointers(
                        functionArgs,
                        name,
                        functionArgRegex,
                        functionArgArrayRegex,
                        functionArgFunctionPointerRegex,
                        functionPointerFrontier,
                        iota
                     );

                     functionPointerDatas.TryAdd(name, new FunctionPointerData() {
                        returnType = returnType,
                        amountOfStars = stars.Count(c => c == '*'),
                        name = name,
                        arrayPart = arrayPart.Success ? arrayPart.Value : null,
                        isVariadic = variadicPart.Success,
                        parameters = parameters
                     });
                  }
               }

               // structs and unions merged. they are merged because unions may add the structFrontier. and structs may add the unionFrontier. so i need to process them together.
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing structs and unions [{path}]...");
                  }

                  // struct
                  MatchCollection typedefStructMatches = typedefStructRegex.Matches(file);
                  MatchCollection structMatches = structRegex.Matches(file);

                  foreach (Match incompleteMatch in structMatches) {
                     string wholeStruct = GetWholeCurlyBraceBlockUntilFollowingSemicolon(file, incompleteMatch.Index);
                     Match match = greedyStructRegex.Match(wholeStruct);

                     string structName = match.Groups["name"].Value;
                     string fields = match.Groups["fields"].Value;
                     structsThatArePresentInOriginalCCode.Add(structName);
                     structFrontier.Enqueue((structName, fields));
                  }

                  foreach (Match incompleteMatch in typedefStructMatches) {
                     string wholeStruct = GetWholeCurlyBraceBlockUntilFollowingSemicolon(file, incompleteMatch.Index);
                     Match match = greedyTypedefStructRegex.Match(wholeStruct);

                     Group nameGroup = match.Groups["name"];
                     string fields = match.Groups["fields"].Value;
                     string typedefdName = match.Groups["typedefdName"].Value;
                     string structName;
                     if (nameGroup.Success) {
                        structName = nameGroup.Value;
                        typedefs.TryAdd(typedefdName, $"struct {structName}");
                     } else {
                        structName = typedefdName;
                     }
                     structsThatArePresentInOriginalCCode.Add(structName);
                     structFrontier.Enqueue((structName, fields));
                  }

                  // union
                  MatchCollection unionMatches = unionRegex.Matches(file);
                  MatchCollection typedefUnionMatches = typedefUnionRegex.Matches(file);

                  foreach (Match incompleteMatch in unionMatches) {
                     string wholeUnion = GetWholeCurlyBraceBlockUntilFollowingSemicolon(file, incompleteMatch.Index);
                     Match match = greedyUnionRegex.Match(wholeUnion);

                     string unionName = match.Groups["name"].Value;
                     string fields = match.Groups["fields"].Value;
                     unionsThatArePresentInOriginalCCode.Add(unionName);
                     unionFrontier.Enqueue((unionName, fields));
                  }

                  foreach (Match incompleteMatch in typedefUnionMatches) {
                     string wholeUnion = GetWholeCurlyBraceBlockUntilFollowingSemicolon(file, incompleteMatch.Index);
                     Match match = greedyTypedefUnionRegex.Match(wholeUnion);

                     Group nameGroup = match.Groups["name"];
                     string fields = match.Groups["fields"].Value;
                     string typedefdName = match.Groups["typedefdName"].Value;
                     string unionName;
                     if (nameGroup.Success) {
                        unionName = nameGroup.Value;
                        typedefs.TryAdd(typedefdName, $"union {unionName}");
                     } else {
                        unionName = typedefdName;
                     }
                     unionsThatArePresentInOriginalCCode.Add(unionName);
                     unionFrontier.Enqueue((unionName, fields));
                  }

                  while (structFrontier.Count > 0 || unionFrontier.Count > 0) {
                     FrontierDequeuedCurrentElementType dequeuedCurrentElementType;
                     string structOrUnionName;
                     string fields;
                     {
                        if (structFrontier.TryDequeue(out (string structName, string fields) currentStruct)) {
                           structOrUnionName = currentStruct.structName;
                           fields = currentStruct.fields;
                           dequeuedCurrentElementType = FrontierDequeuedCurrentElementType.Struct;
                        } else if (unionFrontier.TryDequeue(out (string unionName, string fields) currentUnion)) {
                           structOrUnionName = currentUnion.unionName;
                           fields = currentUnion.fields;
                           dequeuedCurrentElementType = FrontierDequeuedCurrentElementType.Union;
                        } else {
                           throw new UnreachableException("while condition already checked if there is any element in the frontiers");
                        }
                     }

                     fields = fields.Trim();

                     // "fields" may contain complex members. structs inside of structs or unions.

                     // remove all the anonymous struct with variable declarations from the struct or union. and add them to frontier so the anonymous structs can be processed the same way with other structs
                     {
                        for (; ; ) {
                           Match anonymousStructWithVariableDeclarationMatchIncomplete = anonymousStructWithVariableDeclarationRegex.Match(fields);
                           if (!anonymousStructWithVariableDeclarationMatchIncomplete.Success) {
                              break;
                           }
                           string anonymousStructWithVariableDeclarationWholeStruct = GetWholeCurlyBraceBlockUntilFollowingSemicolon(fields, anonymousStructWithVariableDeclarationMatchIncomplete.Index);
                           Match anonymousStructWithVariableDeclarationMatch = greedyAnonymousStructWithVariableDeclarationRegex.Match(anonymousStructWithVariableDeclarationWholeStruct);
                           string variableName = anonymousStructWithVariableDeclarationMatch.Groups["variableName"].Value;
                           string structNameOfAnonymousStructVariable = GetStructNameOfAnonymousStructVariable(variableName, structOrUnionName);
                           fields = fields.Remove(anonymousStructWithVariableDeclarationMatchIncomplete.Index, anonymousStructWithVariableDeclarationWholeStruct.Length)
                                          .Insert(anonymousStructWithVariableDeclarationMatchIncomplete.Index, $"{structNameOfAnonymousStructVariable} {variableName};");

                           structFrontier.Enqueue((structNameOfAnonymousStructVariable, anonymousStructWithVariableDeclarationMatch.Groups["fields"].Value));
                        }
                     }

                     // anonymous structs with no variable declaration
                     {
                        for (; ; ) {
                           Match anonymousStructMatchIncomplete = anonymousStructRegex.Match(fields);
                           if (!anonymousStructMatchIncomplete.Success) {
                              break;
                           }
                           string anonymousStructWholeStruct = GetWholeCurlyBraceBlockUntilFollowingSemicolon(fields, anonymousStructMatchIncomplete.Index);
                           Match anonymousStructMatch = greedyAnonymousStructRegex.Match(anonymousStructWholeStruct);
                           int iotaValueOfVariable = iota.Get();
                           string structNameOfAnonymousStruct = GetStructNameOfAnonymousStructNoVariable(structOrUnionName, iotaValueOfVariable);
                           fields = fields.Remove(anonymousStructMatchIncomplete.Index, anonymousStructWholeStruct.Length)
                                          .Insert(anonymousStructMatchIncomplete.Index, $"{structNameOfAnonymousStruct} __ANONYMOUS__{iotaValueOfVariable};");

                           structFrontier.Enqueue((structNameOfAnonymousStruct, anonymousStructMatch.Groups["fields"].Value));
                        }
                     }

                     // anonymous unions no variable declaration
                     {
                        for (; ; ) {
                           Match anonymousUnionMatchIncomplete = anonymousUnionRegex.Match(fields);
                           if (!anonymousUnionMatchIncomplete.Success) {
                              break;
                           }
                           string anonymousUnionWholeUnion = GetWholeCurlyBraceBlockUntilFollowingSemicolon(fields, anonymousUnionMatchIncomplete.Index);
                           Match anonymousUnionMatch = greedyAnonymousUnionRegex.Match(anonymousUnionWholeUnion);
                           int iotaValueOfVariable = iota.Get();
                           string unionNameOfAnonymousUnion = GetUnionNameOfAnonymousUnionNoVariable(structOrUnionName, iotaValueOfVariable);
                           fields = fields.Remove(anonymousUnionMatchIncomplete.Index, anonymousUnionWholeUnion.Length)
                                          .Insert(anonymousUnionMatchIncomplete.Index, $"{unionNameOfAnonymousUnion} __ANONYMOUS__{iotaValueOfVariable};");

                           unionFrontier.Enqueue((unionNameOfAnonymousUnion, anonymousUnionMatch.Groups["fields"].Value));
                        }
                     }

                     // anonymous unions with variable declaration
                     {
                        for (; ; ) {
                           Match anonymousUnionWithVariableDeclarationMatchIncomplete = anonymousUnionWithVariableDeclarationRegex.Match(fields);
                           if (!anonymousUnionWithVariableDeclarationMatchIncomplete.Success) {
                              break;
                           }
                           string anonymousUnionWithVariableDeclarationWholeUnion = GetWholeCurlyBraceBlockUntilFollowingSemicolon(fields, anonymousUnionWithVariableDeclarationMatchIncomplete.Index);
                           Match anonymousUnionWithVariableDeclarationMatch = greedyAnonymousUnionWithVariableDeclarationRegex.Match(anonymousUnionWithVariableDeclarationWholeUnion);
                           string variableName = anonymousUnionWithVariableDeclarationMatch.Groups["variableName"].Value;
                           string unionNameOfAnonymousUnionVariable = GetUnionNameOfAnonymousUnionVariable(variableName, structOrUnionName);
                           fields = fields.Remove(anonymousUnionWithVariableDeclarationMatchIncomplete.Index, anonymousUnionWithVariableDeclarationWholeUnion.Length)
                                          .Insert(anonymousUnionWithVariableDeclarationMatchIncomplete.Index, $"{unionNameOfAnonymousUnionVariable} {variableName};");

                           unionFrontier.Enqueue((unionNameOfAnonymousUnionVariable, anonymousUnionWithVariableDeclarationMatch.Groups["fields"].Value));
                        }
                     }

                     // nested structs
                     {
                        for (; ; ) {
                           Match nestedStructMatchIncomplete = structRegex.Match(fields);
                           if (!nestedStructMatchIncomplete.Success) {
                              break;
                           }
                           string nestedStructWholeStruct = GetWholeCurlyBraceBlockUntilFollowingSemicolon(fields, nestedStructMatchIncomplete.Index);
                           Match nestedStructMatch = greedyStructRegex.Match(nestedStructWholeStruct);
                           Group variableNameGroup = nestedStructMatch.Groups["variableName"];
                           string nestedStructName = nestedStructMatch.Groups["name"].Value;
                           if (variableNameGroup.Success) {
                              fields = fields.Remove(nestedStructMatchIncomplete.Index, nestedStructWholeStruct.Length)
                                             .Insert(nestedStructMatchIncomplete.Index, $"{nestedStructName} {variableNameGroup.Value};");
                           } else {
                              fields = fields.Remove(nestedStructMatchIncomplete.Index, nestedStructWholeStruct.Length);
                           }

                           // since c# regexes are non-overlapping regexes the first nested type will be missed out but following nested types will already be in the frontier already. so no need to process them again
                           if (!(structDatas.ContainsKey(nestedStructName) || structFrontier.Any(t => t.name == nestedStructName))) {
                              structFrontier.Enqueue((nestedStructName, nestedStructMatch.Groups["fields"].Value));
                           }
                        }
                     }

                     // nested unions
                     {
                        for (; ; ) {
                           Match nestedUnionMatchIncomplete = unionRegex.Match(fields);
                           if (!nestedUnionMatchIncomplete.Success) {
                              break;
                           }
                           string nestedUnionWholeUnion = GetWholeCurlyBraceBlockUntilFollowingSemicolon(fields, nestedUnionMatchIncomplete.Index);
                           Match nestedUnionMatch = greedyUnionRegex.Match(nestedUnionWholeUnion);
                           Group variableNameGroup = nestedUnionMatch.Groups["variableName"];
                           string nestedUnionName = nestedUnionMatch.Groups["name"].Value;
                           if (variableNameGroup.Success) {
                              fields = fields.Remove(nestedUnionMatchIncomplete.Index, nestedUnionWholeUnion.Length)
                                             .Insert(nestedUnionMatchIncomplete.Index, $"{nestedUnionName} {variableNameGroup.Value};");
                           } else {
                              fields = fields.Remove(nestedUnionMatchIncomplete.Index, nestedUnionWholeUnion.Length);
                           }

                           if (!(unionDatas.ContainsKey(nestedUnionName) || unionFrontier.Any(t => t.name == nestedUnionName))) {
                              unionFrontier.Enqueue((nestedUnionName, nestedUnionMatch.Groups["fields"].Value));
                           }
                        }
                     }

                     // nested enums
                     {
                        for (; ; ) {
                           Match nestedEnumMatchIncomplete = enumRegex.Match(fields);
                           if (!nestedEnumMatchIncomplete.Success) {
                              break;
                           }
                           string nestedEnumWholeEnum = GetWholeCurlyBraceBlockUntilFollowingSemicolon(fields, nestedEnumMatchIncomplete.Index);
                           Match nestedEnumMatch = enumRegex.Match(nestedEnumWholeEnum);
                           Group variableNameGroup = nestedEnumMatch.Groups["variableName"];
                           string nestedEnumName = nestedEnumMatch.Groups["name"].Value;
                           if (variableNameGroup.Success) {
                              fields = fields.Remove(nestedEnumMatchIncomplete.Index, nestedEnumWholeEnum.Length)
                                             .Insert(nestedEnumMatchIncomplete.Index, $"{nestedEnumName} {variableNameGroup.Value};");
                           } else {
                              fields = fields.Remove(nestedEnumMatchIncomplete.Index, nestedEnumWholeEnum.Length);
                           }

                           // no need to add nested enum to the frontier because enums dont have any nested types
                           // the enum regex will capture this nested enum as well when processing enums
                        }
                     }

                     // anonymous enums with no variable declaration
                     {
                        // remove it from the struct or union. this will be processed in the enum section including all scopes
                        for (; ; ) {
                           Match anonymousEnumMatch = anonymousEnumRegex.Match(fields);
                           if (!anonymousEnumMatch.Success) {
                              break;
                           }
                           fields = fields.Remove(anonymousEnumMatch.Index, anonymousEnumMatch.Length);
                        }
                     }

                     // anonymous enums with variable declaration
                     {
                        // remove it from the struct or union. this will be processed in the enum section including all scopes
                        for (; ; ) {
                           Match anonymousEnumWithVariableDeclarationMatch = anonymousEnumWithVariableDeclarationRegex.Match(fields);
                           if (!anonymousEnumWithVariableDeclarationMatch.Success) {
                              break;
                           }
                           string variableName = anonymousEnumWithVariableDeclarationMatch.Groups["variableName"].Value;
                           string enumName = GetEnumNameOfAnonymousEnumVariable(variableName, structOrUnionName);
                           fields = fields.Remove(anonymousEnumWithVariableDeclarationMatch.Index, anonymousEnumWithVariableDeclarationMatch.Length)
                                          .Insert(anonymousEnumWithVariableDeclarationMatch.Index, $"{enumName} {variableName};");

                           enumFrontier.Enqueue((enumName, anonymousEnumWithVariableDeclarationMatch.Groups["members"].Value));
                        }
                     }

                     List<(IStructMember member, int matchIndex)> structOrUnionMembers = new List<(IStructMember, int)>(); // matchIndex is used for sorting the members in the order they appear in the file
                     MatchCollection structMemberMatches = structMemberRegex.Matches(fields);
                     foreach (Match structMemberMatch in structMemberMatches) {
                        string name = structMemberMatch.Groups["name"].Value;
                        string type = structMemberMatch.Groups["type"].Value;
                        bool isBitfield = structMemberMatch.Groups["bitfieldValue"].Success;
                        type = type.Trim();
                        name = EnsureVariableIsNotAReservedKeyword(name);

                        structOrUnionMembers.Add((new StructMember() {
                           name = name,
                           type = type,
                           isBitfield = isBitfield
                        }, structMemberMatch.Index));
                     }

                     MatchCollection structOrUnionMemberArrayMatches = structMemberArrayRegex.Matches(fields);
                     foreach (Match structMemberArrayMatch in structOrUnionMemberArrayMatches) {
                        string name = structMemberArrayMatch.Groups["name"].Value;
                        string type = structMemberArrayMatch.Groups["type"].Value;
                        string size = structMemberArrayMatch.Groups["size"].Value;
                        type = type.Trim();
                        name = EnsureVariableIsNotAReservedKeyword(name);

                        structOrUnionMembers.Add((new StructMemberArray() {
                           name = name,
                           type = type,
                           size = size
                        }, structMemberArrayMatch.Index));
                     }

                     structOrUnionMembers.Sort((a, b) => a.matchIndex.CompareTo(b.matchIndex));
                     switch (dequeuedCurrentElementType) {
                        case FrontierDequeuedCurrentElementType.Struct: {
                           structDatas.TryAdd(structOrUnionName, new StructData() {
                              name = structOrUnionName,
                              fields = structOrUnionMembers.Select(t => t.member).ToList(),
                              accessModifier = structsThatArePresentInOriginalCCode.Contains(structOrUnionName) ? "public" : "internal"
                           });
                        } break;
                        case FrontierDequeuedCurrentElementType.Union: {
                           unionDatas.TryAdd(structOrUnionName, new StructData() {
                              name = structOrUnionName,
                              fields = structOrUnionMembers.Select(t => t.member).ToList(),
                              accessModifier = unionsThatArePresentInOriginalCCode.Contains(structOrUnionName) ? "public" : "internal"
                           });
                        } break;
                     }
                  }
               }

               // enums
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing enums [{path}]...");
                  }

                  Regex wordRegex = new Regex(@"\w+");
                  MatchCollection enumMatches = enumRegex.Matches(file);
                  MatchCollection typedefEnumMatches = typedefEnumRegex.Matches(file);

                  foreach (Match enumMatch in enumMatches) {
                     // i have this whole enum check because the regex might overshoot.
                     string wholeEnum = GetWholeCurlyBraceBlockUntilFollowingSemicolon(file, enumMatch.Index);
                     Match wholeEnumMatch = enumRegex.Match(wholeEnum);
                     if (!wholeEnumMatch.Success) {
                        continue;
                     }

                     string enumName = wholeEnumMatch.Groups["name"].Value;
                     string members = wholeEnumMatch.Groups["members"].Value;
                     enumFrontier.Enqueue((enumName, members));
                  }

                  foreach (Match enumMatch in typedefEnumMatches) {
                     string wholeEnum = GetWholeCurlyBraceBlockUntilFollowingSemicolon(file, enumMatch.Index);
                     Match wholeEnumMatch = typedefEnumRegex.Match(wholeEnum);
                     if (!wholeEnumMatch.Success) {
                        continue;
                     }

                     Group nameGroup = wholeEnumMatch.Groups["name"];
                     string members = wholeEnumMatch.Groups["members"].Value;
                     string typedefdName = enumMatch.Groups["typedefdName"].Value;
                     string enumName;
                     if (nameGroup.Success) {
                        enumName = nameGroup.Value;
                        typedefs.TryAdd(typedefdName, $"enum {enumName}");
                     } else {
                        enumName = typedefdName;
                     }
                     enumFrontier.Enqueue((enumName, members));
                  }

                  while (enumFrontier.Count > 0) {
                     (string enumName, string membersString) = enumFrontier.Dequeue();
                     membersString = membersString.Trim();
                     membersString += ","; // add a comma to the end so the last member can be processed. enumMemberRegex requires a comma at the end.

                     enumDatasButPrefixesAreNotRemoved.TryAdd(enumName, new EnumData() {
                        name = enumName,
                        members = ExtractOutEnumMembers(membersString, enumMemberRegex)
                     });

                     // remove the enum name if identifiers have them as prefix
                     // TODO: this part stucks in an infinite loop when processing raylib.h. not the preprocessed one but the actual header.
                     for (; ; ) {
                        MatchCollection wordMatches = wordRegex.Matches(membersString);
                        Match firstWordMatchThatStartWithEnumName = null;
                        foreach (Match wordMatch in wordMatches) {
                           if (wordMatch.Value.StartsWith(enumName, true, null)) {
                              firstWordMatchThatStartWithEnumName = wordMatch;
                              break;
                           }
                        }

                        if (firstWordMatchThatStartWithEnumName == null) {
                           break;
                        }

                        if (firstWordMatchThatStartWithEnumName.Value.Length > /*!=*/ enumName.Length) {
                           if (firstWordMatchThatStartWithEnumName.Value[enumName.Length] == '_') {
                              membersString = membersString.Remove(firstWordMatchThatStartWithEnumName.Index, enumName.Length + 1);
                           } else {
                              membersString = membersString.Remove(firstWordMatchThatStartWithEnumName.Index, enumName.Length);
                           }
                        }
                     }

                     enumDatas.TryAdd(enumName, new EnumData() {
                        name = enumName,
                        members = ExtractOutEnumMembers(membersString, enumMemberRegex)
                     });
                  }

                  // anonymous enums
                  {
                     MatchCollection anonymousEnumMatches = anonymousEnumRegex.Matches(file);
                     MatchCollection anonymousEnumWithVariableDeclarationMatches = anonymousEnumWithVariableDeclarationRegex.Matches(file);
                     foreach (MatchCollection matchCollection in new MatchCollection[] { anonymousEnumMatches, anonymousEnumWithVariableDeclarationMatches }) {
                        foreach (Match enumMatch in matchCollection) {
                           // i have this whole enum check because the regex might overshoot. i think only anonymousEnumRegex can overshoot but to be safe i check both.
                           string wholeEnum = GetWholeCurlyBraceBlockUntilFollowingSemicolon(file, enumMatch.Index);
                           Match wholeEnumMatch = matchCollection == anonymousEnumMatches ? anonymousEnumRegex.Match(wholeEnum) : anonymousEnumWithVariableDeclarationRegex.Match(wholeEnum);
                           if (!wholeEnumMatch.Success) {
                              continue;
                           }

                           string membersString = wholeEnumMatch.Groups["members"].Value;
                           membersString = membersString.Trim();
                           List<EnumMember> enumMembers = new List<EnumMember>();
                           MatchCollection enumMemberMatches = enumMemberRegex.Matches(membersString);
                           foreach (Match match in enumMemberMatches) {
                              string identifier = match.Groups["identifier"].Value;
                              string value;
                              if (match.Groups["value"].Success) {
                                 value = match.Groups["value"].Value;
                              } else {
                                 if (enumMembers.Count == 0) {
                                    value = "0";
                                 } else {
                                    value = $"({enumMembers[enumMembers.Count - 1].identifier}) + 1";
                                 }
                              }

                              // i dont need to this since at the beginning of processing i convert csharp reserved keywords to valid identifiers.
                              // when it comes to c keywords it cant be here if the provided input is a valid c code and i assume it is.
                              // identifier = EnsureVariableIsNotAReservedKeyword(identifier);

                              enumMembers.Add(new EnumMember() {
                                 identifier = identifier,
                                 value = value
                              });
                           }

                           anonymousEnums.Add(enumMembers);
                        }
                     }
                  }
               }

               // opaque structs
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing opaque structs [{path}]...");
                  }

                  MatchCollection opaqueStructMatches = opaqueStructRegex.Matches(file);
                  MatchCollection typedefOpaqueStructMatches = typedefOpaqueStructRegex.Matches(file);

                  foreach (MatchCollection matchCollection in new MatchCollection[] { opaqueStructMatches, typedefOpaqueStructMatches }) {
                     foreach (Match match in matchCollection) {
                        string name = match.Groups["name"].Value;
                        opaqueStructs.Add(name);
                     }
                  }
               }

               // opaque unions
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing opaque unions [{path}]...");
                  }

                  MatchCollection opaqueUnionMatches = opaqueUnionRegex.Matches(file);
                  MatchCollection typedefOpaqueUnionMatches = typedefOpaqueUnionRegex.Matches(file);

                  foreach (MatchCollection matchCollection in new MatchCollection[] { opaqueUnionMatches, typedefOpaqueUnionMatches }) {
                     foreach (Match match in matchCollection) {
                        string name = match.Groups["name"].Value;
                        opaqueUnions.Add(name);
                     }
                  }
               }

               // opaque enums
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing opaque enums [{path}]...");
                  }

                  MatchCollection opaqueEnumMatches = opaqueEnumRegex.Matches(file);
                  MatchCollection typedefOpaqueEnumMatches = typedefOpaqueEnumRegex.Matches(file);

                  foreach (MatchCollection matchCollection in new MatchCollection[] { opaqueEnumMatches, typedefOpaqueEnumMatches }) {
                     foreach (Match match in matchCollection) {
                        string name = match.Groups["name"].Value;
                        opaqueEnums.Add(name);
                     }
                  }
               }
            }
         }

         // remove non opaque types from their corresponding sets
         {
            // opaque structs
            {
               if (showProgress) {
                  Console.WriteLine("Arranging opaque structs...");
               }

               List<string> structsToRemove = new List<string>();
               foreach (string name in opaqueStructs) {
                  if (structDatas.ContainsKey(name)) {
                     structsToRemove.Add(name);
                  }
               }

               opaqueStructs.ExceptWith(structsToRemove);
            }

            // opaque unions
            {
               if (showProgress) {
                  Console.WriteLine("Arranging opaque unions...");
               }

               List<string> unionsToRemove = new List<string>();
               foreach (string name in opaqueUnions) {
                  if (unionDatas.ContainsKey(name)) {
                     unionsToRemove.Add(name);
                  }
               }

               opaqueUnions.ExceptWith(unionsToRemove);
            }

            // opaque enums
            {
               if (showProgress) {
                  Console.WriteLine("Arranging opaque enums...");
               }

               List<string> enumsToRemove = new List<string>();
               foreach (string name in opaqueEnums) {
                  if (enumDatas.ContainsKey(name)) {
                     enumsToRemove.Add(name);
                  }
               }

               opaqueEnums.ExceptWith(enumsToRemove);
            }
         }

         // resolve typedefs and convert to csharp equivalents excluding arrays. you still need to process .arrayPart or .size
         {
            if (showProgress) {
               Console.WriteLine("Resolving typedefs and converting types to C# equivalents...");
            }
            
            // functions
            {
               if (showProgress) {
                  Console.WriteLine("\tFunctions...");
               }
               
               foreach (FunctionData functionData in functionDatas.Values) {
                  functionData.returnType = ResolveTypedefsAndApplyFullConversion(functionData.returnType, typedefs);
                  for (int i = 0; i < functionData.parameters.Count; i++) {
                     if (functionData.parameters[i] is FunctionParameterData) {
                        FunctionParameterData parameterData = functionData.parameters[i] as FunctionParameterData;
                        parameterData.type = ResolveTypedefsAndApplyFullConversion(parameterData.type, typedefs);
                     } else if (functionData.parameters[i] is FunctionParameterArrayData) {
                        FunctionParameterArrayData parameterData = functionData.parameters[i] as FunctionParameterArrayData;
                        parameterData.type = ResolveTypedefsAndApplyFullConversion(parameterData.type, typedefs);
                     } else {
                        throw new UnreachableException();
                     }
                  }
               }
            }

            // function pointers
            {
               if (showProgress) {
                  Console.WriteLine("\tFunction pointers...");
               }
               
               foreach (FunctionPointerData data in functionPointerDatas.Values) {
                  data.returnType = ResolveTypedefsAndApplyFullConversion(data.returnType, typedefs);
                  for (int i = 0; i < data.parameters.Count; i++) {
                     if (data.parameters[i] is FunctionParameterData) {
                        FunctionParameterData parameterData = data.parameters[i] as FunctionParameterData;
                        parameterData.type = ResolveTypedefsAndApplyFullConversion(parameterData.type, typedefs);
                     } else if (data.parameters[i] is FunctionParameterArrayData) {
                        FunctionParameterArrayData parameterData = data.parameters[i] as FunctionParameterArrayData;
                        parameterData.type = ResolveTypedefsAndApplyFullConversion(parameterData.type, typedefs);
                     } else {
                        throw new UnreachableException();
                     }
                  }
               }
            }

            // structs
            {
               if (showProgress) {
                  Console.WriteLine("\tStructs...");
               }
               
               foreach (StructData structData in structDatas.Values) {
                  foreach (IStructMember member in structData.fields) {
                     if (member is StructMember) {
                        StructMember structMember = member as StructMember;
                        structMember.type = ResolveTypedefsAndApplyFullConversion(structMember.type, typedefs);
                     } else if (member is StructMemberArray) {
                        StructMemberArray structMemberArray = member as StructMemberArray;
                        structMemberArray.type = ResolveTypedefsAndApplyFullConversion(structMemberArray.type, typedefs);
                     } else {
                        throw new UnreachableException();
                     }
                  }
               }
            }

            // unions
            {
               if (showProgress) {
                  Console.WriteLine("\tUnions...");
               }
               
               foreach (StructData unionData in unionDatas.Values) {
                  foreach (IStructMember member in unionData.fields) {
                     if (member is StructMember) {
                        StructMember structMember = member as StructMember;
                        structMember.type = ResolveTypedefsAndApplyFullConversion(structMember.type, typedefs);
                     } else if (member is StructMemberArray) {
                        StructMemberArray structMemberArray = member as StructMemberArray;
                        structMemberArray.type = ResolveTypedefsAndApplyFullConversion(structMemberArray.type, typedefs);
                     } else {
                        throw new UnreachableException();
                     }
                  }
               }
            }

            // global const variables
            {
               if (showProgress) {
                  Console.WriteLine("\tGlobal const variables...");
               }
               
               foreach (GlobalConstVariableData data in globalConstVariableDatas.Values) {
                  data.type = ResolveTypedefsAndApplyFullConversion(data.type, typedefs);
                  // TODO: data.values and data.arrayPart may contain types. e.g. sizeof(UserDefinedType)
                  //       i have this problem down below in the MakeBetterSizeString() function too.
               }
            }
         }

         // bitfields with no variable declaration shenanigans
         // lidl explanation. since i added bitfield support to structMemberRegex this type of thing may happen:
         //
         //    unsigned int : 0;
         //
         // here "unsigned" gets registered as .type and "int" get registered as .name which is wrong. this only happens if the type is more than one word.
         // thats what i am checking here. if such bitfield exist remove it. dont process it.
         //
         // NOTE: as far as i understand c standard doesnt force bitfields to be packed. it suggest that if there is enough space then the next bitfield should packed into adjacent bits
         //       what i am doing is i keep every bitfield in an integer. no packing whatsoever.
         //       and i think this breaks the interoperability with c code. i think if a function takes a bitfield struct then the c code, depending on the compiler, will expect the bitfields to be packed but they are not.
         //       and as far as i understand how the bitfield is packed and kept in the memory is implementation dependent so i dont know if there is any way to make it compatible with c code.
         //       and also who cares. no one uses bitfields. no one even knows that they exist Clueless.
         {
            if (showProgress) {
               Console.WriteLine("Bitfield shenanigans...");
            }
            
            foreach (var collection in new Dictionary<string, StructData>.ValueCollection[] { structDatas.Values, unionDatas.Values }) {
               foreach (StructData structData in collection) {
                  List<StructMember> structMembersToRemove = new List<StructMember>();
                  foreach (IStructMember member in structData.fields) {
                     if (member is StructMember structMember) {
                        if (structMember.isBitfield) {
                           if (structMember.name == "int" || structMember.name == "short" || structMember.name == "long" || // int short long only valid bitfield types
                                 structMember.type == "signed" || structMember.type == "unsigned") { // not necessary but causes no harm so we keep it
                              structMembersToRemove.Add(structMember);
                           }
                        }
                     }
                  }

                  foreach (StructMember structMember in structMembersToRemove) {
                     structData.fields.Remove(structMember);
                  }
               }
            }
         }

         // after resolving typedefs the .type will end up either a builtin csharp type or a user defined type. user defined type might be queried from the dictionaries. and i created a set for builtin ones
         if (showProgress) {
            Console.WriteLine("Removing structs/unions/etc that contain unknown types...");
         }

         // remove structs that contain member with an unknown type
         {
            if (showProgress) {
               Console.WriteLine("\tStructs...");
            }
            
            for (; ; ) {
               bool structRemoved = false;
               foreach (var kvp in structDatas) {
                  StructData structData = kvp.Value;
                  string structName = kvp.Key;

                  foreach (IStructMember structMember in structData.fields) {
                     string type;
                     if (structMember is StructMember) {
                        type = (structMember as StructMember).type;
                     } else if (structMember is StructMemberArray) {
                        type = (structMember as StructMemberArray).type;
                     } else {
                        throw new UnreachableException();
                     }
                     type = RemoveStarsFromEnd(type);

                     // does that type exist?
                     if (!DoesTypeExist(type, structDatas, unionDatas, enumDatas, functionPointerDatas, opaqueStructs, opaqueUnions, opaqueEnums)) {
                        // unknown type found. so i should remove this struct otherwise generated c# wont compiler
                        structDatas.Remove(structName); // modifying the collection while iterating over. but it is not a problem since as long as i modify the collection i break out the loop and enter again
                        structRemoved = true;
                        report.removedStructsBecauseTheyContainUnknownTypes.Add(structName);
                        break;
                     }
                  }

                  // need to iterate over the structDatas all over again because the removed struct might wrongly detected as known type in the already scanned structs.
                  if (structRemoved) {
                     break;
                  }
               }

               if (!structRemoved) {
                  break;
               }
            }
         }

         // remove unions that contain member with unknown type
         {
            if (showProgress) {
               Console.WriteLine("\tUnions...");
            }
            
            for (; ; ) {
               bool unionRemoved = false;
               foreach (var kvp in unionDatas) {
                  StructData unionData = kvp.Value;
                  string unionName = kvp.Key;

                  foreach (IStructMember unionMember in unionData.fields) {
                     string type;
                     if (unionMember is StructMember) {
                        type = (unionMember as StructMember).type;
                     } else if (unionMember is StructMemberArray) {
                        type = (unionMember as StructMemberArray).type;
                     } else {
                        throw new UnreachableException();
                     }
                     type = RemoveStarsFromEnd(type);

                     if (!DoesTypeExist(type, structDatas, unionDatas, enumDatas, functionPointerDatas, opaqueStructs, opaqueUnions, opaqueEnums)) {
                        unionDatas.Remove(unionName);
                        unionRemoved = true;
                        report.removedUnionsBecauseTheyContainUnknownTypes.Add(unionName);
                        break;
                     }
                  }

                  if (unionRemoved) {
                     break;
                  }
               }

               if (!unionRemoved) {
                  break;
               }
            }
         }

         // remove functions that contain parameters with unknown type
         {
            if (showProgress) {
               Console.WriteLine("\tFunctions...");
            }
            
            for (; ; ) {
               bool functionRemoved = false;
               foreach (var kvp in functionDatas) {
                  FunctionData functionData = kvp.Value;
                  string functionName = kvp.Key;

                  // return type
                  {
                     string returnType = RemoveStarsFromEnd(functionData.returnType);
                     if (!DoesTypeExist(returnType, structDatas, unionDatas, enumDatas, functionPointerDatas, opaqueStructs, opaqueUnions, opaqueEnums)) {
                        functionDatas.Remove(functionName);
                        functionRemoved = true;
                        report.removedFunctionsBecauseTheyContainUnknownTypes.Add(functionName);
                        break;
                     }
                  }

                  // parameters
                  {
                     foreach (IFunctionParameterData parameterData in functionData.parameters) {
                        string type;
                        if (parameterData is FunctionParameterData) {
                           type = (parameterData as FunctionParameterData).type;
                        } else if (parameterData is FunctionParameterArrayData) {
                           type = (parameterData as FunctionParameterArrayData).type;
                        } else {
                           throw new UnreachableException();
                        }
                        type = RemoveStarsFromEnd(type);

                        if (!DoesTypeExist(type, structDatas, unionDatas, enumDatas, functionPointerDatas, opaqueStructs, opaqueUnions, opaqueEnums)) {
                           functionDatas.Remove(functionName);
                           functionRemoved = true;
                           report.removedFunctionsBecauseTheyContainUnknownTypes.Add(functionName);
                           break;
                        }
                     }

                     if (functionRemoved) {
                        break;
                     }
                  }
               }

               if (!functionRemoved) {
                  break;
               }
            }
         }

         // remove delegates that contain parameters or return types with unknown type
         {
            if (showProgress) {
               Console.WriteLine("\tDelegates...");
            }
            
            for (; ; ) {
               bool functionPointerRemoved = false;
               foreach (var kvp in functionPointerDatas) {
                  FunctionPointerData pointerData = kvp.Value;
                  string functionPointerName = kvp.Key;

                  // return type
                  {
                     string returnType = RemoveStarsFromEnd(pointerData.returnType);
                     if (!DoesTypeExist(returnType, structDatas, unionDatas, enumDatas, functionPointerDatas, opaqueStructs, opaqueUnions, opaqueEnums)) {
                        functionPointerDatas.Remove(functionPointerName);
                        functionPointerRemoved = true;
                        report.removedDelegatesBecauseTheyContainUnknownTypes.Add(functionPointerName);
                        break;
                     }
                  }

                  // parameters
                  {
                     foreach (IFunctionParameterData parameterData in pointerData.parameters) {
                        string type;
                        if (parameterData is FunctionParameterData) {
                           type = (parameterData as FunctionParameterData).type;
                        } else if (parameterData is FunctionParameterArrayData) {
                           type = (parameterData as FunctionParameterArrayData).type;
                        } else {
                           throw new UnreachableException();
                        }
                        type = RemoveStarsFromEnd(type);

                        if (!DoesTypeExist(type, structDatas, unionDatas, enumDatas, functionPointerDatas, opaqueStructs, opaqueUnions, opaqueEnums)) {
                           functionPointerDatas.Remove(functionPointerName);
                           functionPointerRemoved = true;
                           report.removedDelegatesBecauseTheyContainUnknownTypes.Add(functionPointerName);
                           break;
                        }
                     }

                     if (functionPointerRemoved) {
                        break;
                     }
                  }
               }

               if (!functionPointerRemoved) {
                  break;
               }
            }
         }

         // remove global const variables with unknown type
         {
            if (showProgress) {
               Console.WriteLine("\tGlobal const variables...");
            }
            
            for (; ; ) {
               bool globalConstVariableRemoved = false;
               foreach (var kvp in globalConstVariableDatas) {
                  GlobalConstVariableData data = kvp.Value;
                  string name = kvp.Key;

                  string type = RemoveStarsFromEnd(data.type);
                  if (!DoesTypeExist(type, structDatas, unionDatas, enumDatas, functionPointerDatas, opaqueStructs, opaqueUnions, opaqueEnums)) {
                     globalConstVariableDatas.Remove(name);
                     globalConstVariableRemoved = true;
                     report.removedGlobalConstVariablesBecauseTheyContainUnknownTypes.Add(name);
                     break;
                  }
               }

               if (!globalConstVariableRemoved) {
                  break;
               }
            }
         }

         if (showProgress) {
            Console.WriteLine("Preparing C# output...");
         }

         string libName;
         string libNameWithExtension;
         {
            Match match = Regex.Match(soFile, @"(?:.*?/)?(lib)?(?<nameWithExtension>(?<name>\w+)[\w.\-]*?\.so[^/\\]*)");
            libName = match.Groups["name"].Value;
            libNameWithExtension = match.Groups["nameWithExtension"].Value;
         }
         StringBuilder csOutput = new StringBuilder(4 * 1024 * 1024); // 4MB
         csOutput.AppendLine($"/**");
         csOutput.AppendLine($" * This file is auto generated by ctocs (c to cs)");
         csOutput.AppendLine($" * https://github.com/apilatosba/ctocs");
         csOutput.AppendLine($"**/");
         csOutput.AppendLine($"using System;");
         csOutput.AppendLine($"using System.Runtime.InteropServices;");
         csOutput.AppendLine();
         csOutput.AppendLine($"namespace {libName} {{");

         // function pointers / delegates
         {
            if (showProgress) {
               Console.WriteLine($"\tDelegates...");
            }
            
            csOutput.AppendLine("\t// DELEGATES");
            foreach (FunctionPointerData functionPointerData in functionPointerDatas.Values) {
               string functionArgs = GetFunctionArgsAsString(functionPointerData.parameters);

               if (functionPointerData.amountOfStars == 1) {
                  csOutput.AppendLine("\t[UnmanagedFunctionPointer(CallingConvention.Cdecl)]");
                  csOutput.AppendLine($"\tpublic unsafe delegate {functionPointerData.returnType} {functionPointerData.name}({functionArgs}{(functionPointerData.isVariadic ? ", __arglist" : "")});");
               } else {
                  // TODO: idk if above implementation would work for star amount other than 1. test it if it works then you can remove the if check.
                  //       maybe if it comes from a function parameter then i can declare the delegate as above and put the necessary stars in the function parameter. idk if it will work tho. need testing.
                  //       i think same concern goes for .arrayPart as well
                  report.functionPointersWithAmountOfStarsDifferentThanOne.Add(functionPointerData.name);
               }
            }
         }

         csOutput.AppendLine();
         csOutput.AppendLine($"\tpublic static unsafe partial class Native {{");
         csOutput.AppendLine($"\t\tpublic const string LIBRARY_NAME = @\"{libNameWithExtension}\";");

         // defines
         {
            if (showProgress) {
               Console.WriteLine($"\tDefines...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t\t// DEFINES");
            foreach (var kvp in singleLineDefines) {               
               if (kvp.Value.StartsWith("0x") && int.TryParse(kvp.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hexValue)) { // you have remove the 0x in the beginning otherwise int.tryparse doesnt work
                  csOutput.AppendLine($"\t\tpublic const uint {kvp.Key} = {kvp.Value};");
                  singleLineDefineTypes.Add(kvp.Key, typeof(uint));
               } else if (kvp.Value.StartsWith("0b") && int.TryParse(kvp.Value.AsSpan(2), NumberStyles.BinaryNumber, CultureInfo.InvariantCulture, out int binaryValue)) {
                  csOutput.AppendLine($"\t\tpublic const uint {kvp.Key} = {kvp.Value};");
                  singleLineDefineTypes.Add(kvp.Key, typeof(uint));
               } else if (int.TryParse(kvp.Value, out int intValue)) {
                  csOutput.AppendLine($"\t\tpublic const int {kvp.Key} = {intValue};");
                  singleLineDefineTypes.Add(kvp.Key, typeof(int));
               } else if (float.TryParse(kvp.Value, out float floatValue)) {
                  csOutput.AppendLine($"\t\tpublic const float {kvp.Key} = {floatValue}f;");
                  singleLineDefineTypes.Add(kvp.Key, typeof(float));
               } else if (double.TryParse(kvp.Value, out double doubleValue)) {
                  csOutput.AppendLine($"\t\tpublic const double {kvp.Key} = {doubleValue}d;");
                  singleLineDefineTypes.Add(kvp.Key, typeof(double));
               } else if (kvp.Value.StartsWith('"') && kvp.Value.EndsWith('"')) {
                  csOutput.AppendLine($"\t\tpublic const string {kvp.Key} = {kvp.Value};");
                  singleLineDefineTypes.Add(kvp.Key, typeof(string));
               } else if (kvp.Value.StartsWith('\'') && kvp.Value.EndsWith('\'')) {
                  csOutput.AppendLine($"\t\tpublic const char {kvp.Key} = {kvp.Value};");
                  singleLineDefineTypes.Add(kvp.Key, typeof(char));
               } else {
                  // lets check int float double considering suffixes
                  const int MAX_SUFFIX_LENGTH = 3;
                  StringBuilder suffixBuilder = new StringBuilder();
                  StringBuilder suffixRemoved = new StringBuilder(kvp.Value);
                  for (int i = 0; i < MAX_SUFFIX_LENGTH; i++) {
                     if (char.IsAsciiLetter(suffixRemoved[suffixRemoved.Length - 1])) {
                        suffixBuilder.Append(suffixRemoved[suffixRemoved.Length - 1]);
                        suffixRemoved.Remove(suffixRemoved.Length - 1, 1);
                     } else {
                        break;
                     }
                  }
                  string suffix = ReverseString(suffixBuilder.ToString());
                  suffix = suffix.ToLower();

                  if (suffix == "f") {
                     if (float.TryParse(suffixRemoved.ToString(), out float floatValue2)) {
                        csOutput.AppendLine($"\t\tpublic const float {kvp.Key} = {floatValue2}f;");
                        singleLineDefineTypes.Add(kvp.Key, typeof(float));
                     }
                  } else if (suffix == "ll") {
                     if (long.TryParse(suffixRemoved.ToString(), out long longValue)) {
                        csOutput.AppendLine($"\t\tpublic const long {kvp.Key} = {longValue};");
                        singleLineDefineTypes.Add(kvp.Key, typeof(long));
                     }
                  } else if (suffix == "ull" || suffix == "llu" || suffix == "ul" || suffix == "lu") {
                     if (ulong.TryParse(suffixRemoved.ToString(), out ulong ulongValue)) {
                        csOutput.AppendLine($"\t\tpublic const ulong {kvp.Key} = {ulongValue};");
                        singleLineDefineTypes.Add(kvp.Key, typeof(ulong));
                     }
                  } else if (suffix == "l") {
                     // it could be anything. if it contains a dot then lets do double otherwise do long.
                     if (suffixRemoved.ToString().Contains('.')) {
                        if (double.TryParse(suffixRemoved.ToString(), out double doubleValue2)) {
                           csOutput.AppendLine($"\t\tpublic const double {kvp.Key} = {doubleValue2}d;");
                           singleLineDefineTypes.Add(kvp.Key, typeof(double));
                        }
                     } else {
                        if (long.TryParse(suffixRemoved.ToString(), out long longValue2)) {
                           csOutput.AppendLine($"\t\tpublic const long {kvp.Key} = {longValue2};");
                           singleLineDefineTypes.Add(kvp.Key, typeof(long));
                        }
                     }
                  } else {
                     report.unableToParseDefines.Add(kvp.Key, kvp.Value);
                  }
               }
            }
            
            // if there are defines that are written in terms of other defines, then we can write them as well.
            csOutput.AppendLine();
            csOutput.AppendLine("\t\t// DEFINES THAT ARE WRITTEN IN TERMS OF OTHER DEFINES");
            foreach (var kvp in defines) {
               if (singleLineDefineTypes.ContainsKey(kvp.Key)) { // singleLineDefineTypes instead of singleLineDefines because singleLineDefineTypes only contains only the written ones and thats what i want.
                  continue;
               }

               Regex wordRegex = new Regex(@"\w+");
               MatchCollection words = wordRegex.Matches(kvp.Value);
               bool anUnknownWordExists = false;
               Type typeOfKnownWord = null;
               foreach (Match word in words) { // TODO: if unknown word is a literal then it is fine. implement it.
                  if (!singleLineDefineTypes.ContainsKey(word.Value)) {
                     anUnknownWordExists = true;
                     break;
                  } else {
                     if (singleLineDefineTypes.TryGetValue(word.Value, out Type value)) {
                        typeOfKnownWord = value;
                     }
                  }
               }
               if (!anUnknownWordExists) {
                  csOutput.AppendLine($"\t\tpublic const {typeOfKnownWord.FullName} {kvp.Key} = {kvp.Value};"); // TODO: no unsafe?
               } else {
                  report.unableToParseDefines.TryAdd(kvp.Key, kvp.Value);
               }
            }
         }

         // global const variables
         {
            if (showProgress) {
               Console.WriteLine($"\tGlobal const variables...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine("\t\t// GLOBAL CONST VARIABLES");

            foreach (GlobalConstVariableData data in globalConstVariableDatas.Values) {
               string value = ConvertCGlobalConstVariableValueToCSharpValue(data.value, dotMemberEqualsRegex, noNewBeforeCurlyBraceRegex, openCurlyBraceRegex);
               if (data.arrayPart == null) {
                  if (data.value.StartsWith('{')) {
                     csOutput.AppendLine($"\t\tpublic unsafe static readonly {data.type} {data.name} = {value};");
                  } else {
                     csOutput.AppendLine($"\t\tpublic unsafe const {data.type} {data.name} = {data.value};"); // TODO: i think this is not gonna work if data.value is a string literal. need to check
                  }
               } else {
                  List<string> parts = ExtractOutArrayParts(data.arrayPart, openSquareBracketRegex); // this function is overkill here i only need the brace count. i use it because i write and then realized it isnt necessary and didnt want to let it stay unused
                  string brackets;
                  {
                     StringBuilder bracketsBuilder = new StringBuilder();
                     for (int i = 0; i < parts.Count; i++) {
                        bracketsBuilder.Append("[]");
                     }
                     brackets = bracketsBuilder.ToString();
                  }
                  csOutput.AppendLine($"\t\tpublic unsafe static readonly {data.type}{brackets} {data.name} = {value};");
               }
            }
         }

         // enums extracted out
         {
            if (showProgress) {
               Console.WriteLine($"\tEnums (extracted out)...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t\t// ENUMS (extracted out)");
            foreach (EnumData enumData in enumDatasButPrefixesAreNotRemoved.Values) {
               if (enumData.name.StartsWith("__ANONYMOUS__") && enumData.name.EndsWith("_ENUM")) {
                  continue;
               }

               List<EnumMember> enumMembersWithExplicitValues = GetEnumMembersWithExplicitValues(enumData.members);
               foreach (EnumMember enumMember in enumMembersWithExplicitValues) {
                  csOutput.AppendLine($"\t\tpublic const int {enumMember.identifier} = {enumMember.value};");
               }
            }
         }

         // anonymous enums
         {
            if (showProgress) {
               Console.WriteLine($"\tAnonymous enums...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t\t// ANONYMOUS ENUMS");
            foreach (List<EnumMember> anonymousEnum in anonymousEnums) {
               foreach (EnumMember enumMember in anonymousEnum) {
                  csOutput.AppendLine($"\t\tpublic const int {enumMember.identifier} = {enumMember.value};");
               }
            }
         }

         // functions
         {
            if (showProgress) {
               Console.WriteLine($"\tFunctions...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t\t// FUNCTIONS");
            foreach (var kvp in functionDatas) {
               string functionName = kvp.Key;
               FunctionData functionData = kvp.Value;

               string functionArgs = GetFunctionArgsAsString(functionData.parameters);

               // NOTE: apparently calling a function with __arglist is not supported in c#.
               //       instead what you need to do is create a new dllimport entry for each different signature of the function.
               //       lets give a simple example. consider printf(const char*, ...)
               //
               //          [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "printf")]
               //          public static extern int printf(String format, __arglist);
               //
               //       lets say i want to call it like so:       
               //          printf("%i", 5);
               //
               //       then you need to create a new dllimport entry for this signature like following:
               //
               //          [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "printf")]
               //          public static extern int printf(String format, int arg1);
               //
               //       please note here that CallingConvention = CallingConvention.Cdecl is necessary
               //
               //       but if instead calling a function with __arglist was supported then you could call it like so:
               //          printf("%i", __arglist(5));
               //
               //       and you wouldnt need to create dllimport entries for each signature.
               //       i am still clueless since __arglist is undocumented. maybe there is a way to call variadic functions Clueless
               csOutput.AppendLine($"\t\t[DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = \"{functionName}\")]");
               csOutput.AppendLine($"\t\tpublic static extern {functionData.returnType} {functionData.name}({functionArgs}{(functionData.isVariadic ? ", __arglist" : "")});");
            }
         }

         csOutput.AppendLine("\t}");

         // structs
         {
            if (showProgress) {
               Console.WriteLine($"\tStructs...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// STRUCTS");
            foreach (StructData structData in structDatas.Values) {
               if (structData.fields.Count == 0) { // TODO: i dont think i should do this
                  report.removedStructsBecauseTheyDontHaveAnyFields.Add(structData.name);
                  continue; // assuming the original struct is not empty and my regex matched it wrong. so skip
               }

               // csOutput.AppendLine($"  [StructLayout(LayoutKind.Explicit)]");
               // csOutput.AppendLine($"  {structData.accessModifier} unsafe partial struct {structData.name} {{"); // Inconsistent accessibility: field type 'Outer_inner_struct' is less accessible than field 'Outer.inner' [Main]csharp(CS0052)
               csOutput.AppendLine($"\tpublic unsafe partial struct {structData.name} {{");
               for (int i = 0; i < structData.fields.Count; i++) {
                  if (structData.fields[i] is StructMember) {
                     StructMember member = structData.fields[i] as StructMember;
                     // StringBuilder fieldOffsetBuilder = new StringBuilder();
                     // fieldOffsetBuilder.Append($"     [FieldOffset(");

                     // if (i == 0) {
                     //    fieldOffsetBuilder.Append('0');
                     // }

                     // // iterate over previous fields
                     // for (int j = 0; j < i; j++) {
                     //    fieldOffsetBuilder.Append($"sizeof({structData.fields[j].type})");
                     //    if (j != i - 1) {
                     //       fieldOffsetBuilder.Append(" + ");
                     //    }
                     // }
                     // fieldOffsetBuilder.Append($")]");

                     // csOutput.AppendLine(fieldOffsetBuilder.ToString());

                     // extract out anonymous structs. because thats how you access them in original C code.
                     if (member.type.StartsWith("__ANONYMOUS__") && member.type.EndsWith("_STRUCT")) {
                        StructData anonymousStructData = structDatas[member.type];
                        foreach (StructMember anonymousStructMember in anonymousStructData.fields) {
                           csOutput.AppendLine($"\t\tpublic {anonymousStructMember.type} {anonymousStructMember.name};");
                        }
                     } else {
                        csOutput.AppendLine($"\t\tpublic {member.type} {member.name};");
                     }
                  } else if (structData.fields[i] is StructMemberArray) {
                     StructMemberArray memberArray = structData.fields[i] as StructMemberArray;
                     string memberArraySize = MakeBetterSizeString(memberArray.size, typedefs);
                     if (TypeInfo.allowedFixedSizeBufferTypes.Contains(memberArray.type)) {
                        csOutput.AppendLine($"\t\tpublic fixed {memberArray.type} {memberArray.name}[{memberArraySize}];");
                     } else {
                        report.incompleteStructsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers.Add(structData.name);
                        // TODO: idk what to do
                     }
                  } else {
                     throw new UnreachableException();
                  }
               }
               csOutput.AppendLine("\t}");
            }
         }

         // opaque structs
         {
            if (showProgress) {
               Console.WriteLine($"\tOpaque structs...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// OPAQUE STRUCTS");
            foreach (string opaqueStruct in opaqueStructs) {
               csOutput.AppendLine($"\tpublic partial struct {opaqueStruct} {{ }}");
            }
         }

         // unions
         {
            if (showProgress) {
               Console.WriteLine($"\tUnions...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// UNIONS");
            foreach (StructData unionData in unionDatas.Values) {
               if (unionData.fields.Count == 0) { // TODO: prolly shouldnt do this. same reason as structs
                  report.removedUnionsBecauseTheyDontHaveAnyFields.Add(unionData.name);
                  continue;
               }

               csOutput.AppendLine($"\t[StructLayout(LayoutKind.Explicit)]");
               csOutput.AppendLine($"\tpublic unsafe partial struct {unionData.name} {{");
               for (int i = 0; i < unionData.fields.Count; i++) {
                  if (unionData.fields[i] is StructMember) {
                     StructMember member = unionData.fields[i] as StructMember;
                     csOutput.AppendLine($"\t\t[FieldOffset(0)]");
                     csOutput.AppendLine($"\t\tpublic {member.type} {member.name};");
                  } else if (unionData.fields[i] is StructMemberArray) {
                     StructMemberArray memberArray = unionData.fields[i] as StructMemberArray;
                     if (TypeInfo.allowedFixedSizeBufferTypes.Contains(memberArray.type)) {
                        string memberArraySize = MakeBetterSizeString(memberArray.size, typedefs);
                        csOutput.AppendLine($"\t\t[FieldOffset(0)]");
                        csOutput.AppendLine($"\t\tpublic fixed {memberArray.type} {memberArray.name}[{memberArraySize}];");
                     } else {
                        report.incompleteUnionsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers.Add(unionData.name);
                        // TODO: donk
                     }
                  } else {
                     throw new UnreachableException();
                  }
               }
               csOutput.AppendLine("\t}");
            }
         }

         // opaque unions
         {
            if (showProgress) {
               Console.WriteLine($"\tOpaque unions...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// OPAQUE UNIONS");
            foreach (string opaqueUnion in opaqueUnions) {
               csOutput.AppendLine($"\tpublic partial struct {opaqueUnion} {{ }}");
            }
         }

         // enums
         {
            if (showProgress) {
               Console.WriteLine($"\tEnums...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// ENUMS");
            foreach (EnumData enumData in enumDatas.Values) {
               if (enumData.members.Count == 0) { // TODO: prolly shouldnt do this. same reason as structs
                  report.removedEnumsBecauseTheyDontHaveAnyIdentifier.Add(enumData.name);
                  continue; // assuming the original enum is not empty and my regex matched it wrong. so skip
               }

               csOutput.AppendLine($"\tpublic enum {enumData.name} {{");
               foreach (EnumMember enumMember in enumData.members) {
                  csOutput.AppendLine($"\t\t{enumMember.identifier}{(string.IsNullOrEmpty(enumMember.value) ? "" : $" = {enumMember.value}")},");
               }
               csOutput.AppendLine("\t}");
            }
         }

         // opaque enums
         {
            if (showProgress) {
               Console.WriteLine($"\tOpaque enums...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// OPAQUE ENUMS");
            foreach (string opaqueEnum in opaqueEnums) {
               csOutput.AppendLine($"\tpublic enum {opaqueEnum} {{ }}");
            }
         }

         // Safe wrapper
         {
            if (showProgress) {
               Console.WriteLine($"\tSafe wrapper...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// SAFE WRAPPER");
            csOutput.AppendLine("\tpublic static unsafe partial class Safe {");

            // function parameters with single star pointers and single dimension arrays
            {
               foreach (FunctionData functionData in functionDatas.Values) {
                  if (functionData.isVariadic) {
                     continue;
                  }
                  
                  List<FunctionParameterData> newParameters = new List<FunctionParameterData>();
                  List<int> indicesOfParametersToBeModified = new List<int>();
                  for (int i = 0; i < functionData.parameters.Count; i++) {
                     if (functionData.parameters[i] is FunctionParameterData) {
                        FunctionParameterData parameterData = functionData.parameters[i] as FunctionParameterData;
                        if (parameterData.type.Count(c => c == '*') == 1 && IsBasicType(parameterData.type.Substring(0, parameterData.type.Length - 1))) {
                           newParameters.Add(new FunctionParameterData() {
                              type = $"{parameterData.type.Replace("*", "[]")}",
                              name = parameterData.name
                           });
                           indicesOfParametersToBeModified.Add(i);
                        } else {
                           newParameters.Add(parameterData);
                        }
                     } else if (functionData.parameters[i] is FunctionParameterArrayData) {
                        FunctionParameterArrayData parameterData = functionData.parameters[i] as FunctionParameterArrayData;
                        if (parameterData.type.Count(c => c == '*') == 0 &&
                              parameterData.arrayPart.Count(c => c == '[') == 1) {
                           newParameters.Add(new FunctionParameterData() {
                              type = $"{parameterData.type}[]",
                              name = parameterData.name
                           });
                           indicesOfParametersToBeModified.Add(i);
                        } else {
                           string type = GetTypeStringOfFunctionParameterArray(parameterData, out _);
                           newParameters.Add(new FunctionParameterData() {
                              type = type,
                              name = parameterData.name
                           });
                        }
                     } else {
                        throw new UnreachableException();
                     }
                  }

                  if (indicesOfParametersToBeModified.Count == 0) {
                     continue;
                  }

                  csOutput.AppendLine($"\t\tpublic static {functionData.returnType} {functionData.name}({string.Join(", ", newParameters.Select(p => $"{p.type} {p.name}"))}) {{");
                  foreach (int index in indicesOfParametersToBeModified) {
                     if (functionData.parameters[index] is FunctionParameterData) {
                        FunctionParameterData parameterData = functionData.parameters[index] as FunctionParameterData;
                        csOutput.AppendLine($"\t\t\tfixed({parameterData.type} {parameterData.name}Ptr = &{parameterData.name}[0])");
                     } else if (functionData.parameters[index] is FunctionParameterArrayData) {
                        FunctionParameterArrayData parameterData = functionData.parameters[index] as FunctionParameterArrayData;
                        csOutput.AppendLine($"\t\t\tfixed({parameterData.type}* {parameterData.name}Ptr = &{parameterData.name}[0])");
                     } else {
                        throw new UnreachableException();
                     }
                  }
                  csOutput.AppendLine($"\t\t\t\t{(functionData.returnType == "void" ? "" : "return ")}Native.{functionData.name}({string.Join(", ", newParameters.Select((p, i) => indicesOfParametersToBeModified.Contains(i) ? $"{p.name}Ptr" : p.name))});");
                  csOutput.AppendLine("\t\t}");
               }
            }
            csOutput.AppendLine("\t}");
         }

         csOutput.AppendLine("}");


         if (showProgress) {
            Console.WriteLine($"Creating the output directory...");
         }
         string outputDirectory = $"ctocs_{libName}";
         Directory.CreateDirectory(outputDirectory);

         if (showProgress) {
            Console.WriteLine($"Writing to file...");
         }
         File.WriteAllText(Path.Combine(outputDirectory, $"{libName}.cs"), csOutput.ToString());

         // is it just me or dotnet format kinda sucks
         // if (showProgress) {
         //    Console.WriteLine($"Formatting the code...");
         // }
         // Process.Start("dotnet", $"format whitespace --folder {outputDirectory}").WaitForExit();

         // report
         {
            if (showProgress) {
               Console.WriteLine("Preparing the report...");
            }

            StringBuilder reportBuilder = new StringBuilder();

            reportBuilder.AppendLine($"Report of - ctocs {string.Join(' ', args)}");

            if (report.definesThatAreDefinedMoreThanOnce.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Defines that are defined more than once thus not included in the csharp output (probably because of ifdef guards):");
               foreach (string define in report.definesThatAreDefinedMoreThanOnce) {
                  reportBuilder.AppendLine($"\t{define}");
               }
            }

            if (report.typedefdFunctionsOrWronglyCapturedFunctionPointers.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Typedefd functions or wrongly captured function pointers not considered as functions:");
               foreach (string typedefdFunction in report.typedefdFunctionsOrWronglyCapturedFunctionPointers) {
                  reportBuilder.AppendLine($"\t{typedefdFunction}");
               }
            }

            if (report.functionsThatExistInHeaderFileButNotExposedInSOFile.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Functions that exists in the header files but not exposed in the .so file:");
               foreach (string function in report.functionsThatExistInHeaderFileButNotExposedInSOFile) {
                  reportBuilder.AppendLine($"\t{function}");
               }
            }

            if (report.removedStructsBecauseTheyContainUnknownTypes.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Structs that contain unknown types thus not included in the csharp output:");
               foreach (string structName in report.removedStructsBecauseTheyContainUnknownTypes) {
                  reportBuilder.AppendLine($"\t{structName}");
               }
            }

            if (report.removedUnionsBecauseTheyContainUnknownTypes.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Unions that contain unknown types thus not included in the csharp output:");
               foreach (string unionName in report.removedUnionsBecauseTheyContainUnknownTypes) {
                  reportBuilder.AppendLine($"\t{unionName}");
               }
            }

            if (report.removedFunctionsBecauseTheyContainUnknownTypes.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Functions that contain unknown types thus not included in the csharp output:");
               foreach (string functionName in report.removedFunctionsBecauseTheyContainUnknownTypes) {
                  reportBuilder.AppendLine($"\t{functionName}");
               }
            }

            if (report.removedDelegatesBecauseTheyContainUnknownTypes.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Delegates that contain unknown types thus not included in the csharp output:");
               foreach (string delegateName in report.removedDelegatesBecauseTheyContainUnknownTypes) {
                  reportBuilder.AppendLine($"\t{delegateName}");
               }
            }

            if (report.removedGlobalConstVariablesBecauseTheyContainUnknownTypes.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Global const variables that contain unknown types thus not included in the csharp output:");
               foreach (string globalConstVariableName in report.removedGlobalConstVariablesBecauseTheyContainUnknownTypes) {
                  reportBuilder.AppendLine($"\t{globalConstVariableName}");
               }
            }

            if (report.functionPointersWithAmountOfStarsDifferentThanOne.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Function pointers with amount of stars different than one thus not included in the csharp output because idk what to do with thme yet:");
               foreach (string functionPointer in report.functionPointersWithAmountOfStarsDifferentThanOne) {
                  reportBuilder.AppendLine($"\t{functionPointer}");
               }
            }

            if (report.unableToParseDefines.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Defines that are unable to be parsed:");
               foreach (string define in report.unableToParseDefines.Keys) {
                  reportBuilder.AppendLine($"\t{define}");
               }

               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Values of the previous defines:");
               foreach (var kvp in report.unableToParseDefines) {
                  reportBuilder.AppendLine($"\t{kvp.Key} = {kvp.Value}");
                  reportBuilder.AppendLine();
               }
            }

            if (report.removedStructsBecauseTheyDontHaveAnyFields.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Removed structs because they dont have any fields:");
               foreach (string structName in report.removedStructsBecauseTheyDontHaveAnyFields) {
                  reportBuilder.AppendLine($"\t{structName}");
               }
            }

            if (report.removedUnionsBecauseTheyDontHaveAnyFields.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Removed unions because they dont have any fields:");
               foreach (string unionName in report.removedUnionsBecauseTheyDontHaveAnyFields) {
                  reportBuilder.AppendLine($"\t{unionName}");
               }
            }

            if (report.removedEnumsBecauseTheyDontHaveAnyIdentifier.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Removed enums because they dont have any identifier:");
               foreach (string enumName in report.removedEnumsBecauseTheyDontHaveAnyIdentifier) {
                  reportBuilder.AppendLine($"\t{enumName}");
               }
            }

            if (report.incompleteStructsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Incomplete structs because they contain an array with a type not allowed in fixed size buffers:");
               foreach (string structName in report.incompleteStructsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers) {
                  reportBuilder.AppendLine($"\t{structName}");
               }
            }

            if (report.incompleteUnionsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers.Count > 0) {
               reportBuilder.AppendLine();
               reportBuilder.AppendLine("Incomplete unions because they contain an array with a type not allowed in fixed size buffers:");
               foreach (string unionName in report.incompleteUnionsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers) {
                  reportBuilder.AppendLine($"\t{unionName}");
               }
            }

            if (showProgress) {
               Console.WriteLine("Writing the report...");
            }
            File.WriteAllText(Path.Combine(outputDirectory, $"{libName}_report.txt"), reportBuilder.ToString());
         }
   
         Console.ForegroundColor = ConsoleColor.Green;
         Console.WriteLine($"Done. Output is in \"{outputDirectory}\"");
      }

      static void PrintHelp() {
         string help =
         """
         Usage: ctocs [options] sofile <.so file> hfiles <list of .h files>,, phfiles <list of preprocessed .h files>,,
                ctocs [--help | -h]

         Options:
            --help, -h: Show this help message.
            no-show-progress: Do not show progress text.
         Examples:
            ctocs hfiles file1.h file2.h,, sofile libexample.so phfiles pfile1.h pfile2.h,,
         """;
         Console.WriteLine(help);
      }

      static void PrintHelpAndExit() {
         PrintHelp();
         Environment.Exit(0);
      }

      static string ReverseString(string s) {
         char[] charArray = s.ToCharArray();
         Array.Reverse(charArray);
         return new string(charArray);
      }

      /// <summary>
      /// Result is a csharp type <br />
      /// If <paramref name="ctype"/> doesnt contain white spaces or star and if <paramref name="ctype"/> is not a c type but a gibberish string then this function should return the same string. This feature of this function is utilized in <see cref="MakeBetterSizeString(in string, Dictionary{string, string})"/>
      /// </summary>
      static string ResolveTypedefsAndApplyFullConversion(string ctype, Dictionary<string, string> typedefs) {
         string result = ConvertWhiteSpacesToSingleSpace(ctype);
         result = result.Trim();
         bool isPointer = result.EndsWith('*');

         result = isPointer ? RemoveStarsFromEnd(result) : result;
         // this loop will stuck in an infinite loop if there is any cycles in the typedefs. but i assume that the provided header files compiles without any errors (that is a valid c program) which means there is no cyclic typedefs.
         for (; ; ) {
            if (!typedefs.TryGetValue(result, out string newType)) {
               break;
            }
            result = newType;
         }

         // this requires the result string to be spaced with only one space between each word.
         if (result.StartsWith("signed") && !result.StartsWith("signed char")) {
            result = result.Remove(0, "signed ".Length);
         }
         result = TypeInfo.basicTypes.TryGetValue(result, out string convertedType) ? convertedType : result;

         if (result.StartsWith("struct ")) {
            result = result.Remove(0, "struct ".Length);
         } else if (result.StartsWith("union ")) {
            result = result.Remove(0, "union ".Length);
         } else if (result.StartsWith("enum ")) {
            result = result.Remove(0, "enum ".Length);
         }

         if (isPointer) {
            int starCount = CountStarsAtEndIgnoreWhiteSpace(ctype);
            StringBuilder resultBuilder = new StringBuilder(result);
            for (int i = 0; i < starCount; i++) {
               resultBuilder.Append('*');
            }
            result = resultBuilder.ToString();
         }

         return result;
      }

      static string ConvertWhiteSpacesToSingleSpace(string s) {
         return Regex.Replace(s, @"\s+", " ");
      }

      static string RemoveStarsFromEnd(string s) {
         return s.TrimEnd('*', ' ');
      }

      static int CountStarsAtEndIgnoreWhiteSpace(string s) {
         int count = 0;
         for (int i = s.Length - 1; i >= 0; i--) {
            if (s[i] == '*') {
               count++;
            } else if (char.IsWhiteSpace(s[i])) {
               continue;
            } else {
               break;
            }
         }
         return count;
      }

      // this function is used for 
      //   in Safe wrapper class if parameter is a pointer then it is converted to an array.
      //   but only if it is a basic type. int* -> int[]
      // TODO: move this to TypeInfo. i think using TypeInfo.csharpReservedKeywordsThatAreTypes is fine
      static bool IsBasicType(string type) {
         return new string[] {
            "int", "uint", "long", "ulong", "short", "ushort", "char", "float", "double", "byte", "sbyte", "bool"
         }.Contains(type);
      }

      /// <summary>
      /// Generic version of <see cref="GetWholeCurlyBraceBlockUntilFollowingSemicolon(string, int)"/> <br />
      /// If <paramref name="endCharacter"/> is null then it will return to the end of block.
      /// </summary>
      static string GetWholeBlockUntilFollowingCharacter(string file, int startIndex, char blockBegin, char blockEnd, char? endCharacter, out int indexOfBlockEnd) {
         bool enteredFirstBrace = false;
         int openBraceCount = 0;
         int i = startIndex;
         for (; i < file.Length; i++) {
            if (file[i] == blockBegin) {
               enteredFirstBrace = true;
               openBraceCount++;
            } else if (file[i] == blockEnd) {
               openBraceCount--;
            }

            if (openBraceCount == 0 && enteredFirstBrace) {
               break;
            }

            if (i == file.Length - 1) {
               throw new Exception("Block never ended");
            }
         }

         indexOfBlockEnd = i;

         if (endCharacter != null) {
            for (; i < file.Length; i++) {
               if (file[i] == endCharacter) {
                  break;
               }

               if (i == file.Length - 1) {
                  throw new Exception("endCharacter never found");
               }
            }
         }

         return file.Substring(startIndex, i - startIndex + 1);
      }

      /// <summary>
      /// <see cref="GetWholeBlockUntilFollowingCharacter(string, int, char, char, char?, out int)"/> but with multiple ending characters
      /// </summary>

      /// <summary>
      /// Generic version of <see cref="GetWholeCurlyBraceBlockUntilFollowingSemicolon(string, int)"/> <br />
      /// If <paramref name="endCharacter"/> is null then it will return to the end of block.
      /// </summary>
      static string GetWholeBlockUntilFollowingCharacters(string file, int startIndex, char blockBegin, char blockEnd, out int indexOfBlockEnd, params char[] endCharacters) {
         bool enteredFirstBrace = false;
         int openBraceCount = 0;
         int i = startIndex;
         for (; i < file.Length; i++) {
            if (file[i] == blockBegin) {
               enteredFirstBrace = true;
               openBraceCount++;
            } else if (file[i] == blockEnd) {
               openBraceCount--;
            }

            if (openBraceCount == 0 && enteredFirstBrace) {
               break;
            }

            if (i == file.Length - 1) {
               throw new Exception("Block never ended");
            }
         }

         indexOfBlockEnd = i;

         if (endCharacters != null) {
            for (; i < file.Length; i++) {
               if (endCharacters.Contains(file[i])) {
                  break;
               }

               if (i == file.Length - 1) {
                  throw new Exception("endCharacter never found");
               }
            }
         }

         return file.Substring(startIndex, i - startIndex + 1);
      }


      /// <summary>
      /// Uses curly brace count. The string returned starts from file[startIndex] and ends at the first semicolon after the last closing curly brace.
      /// </summary>
      static string GetWholeCurlyBraceBlockUntilFollowingSemicolon(string file, int startIndex) {
         return GetWholeBlockUntilFollowingCharacter(file, startIndex, '{', '}', ';', out _);
      }

      // dont use this function for function pointers because name is enclosed with braces in function pointers and after name ends this function stops which is probably not what you want.
      static string GetWholeParanthesisBlockUntilFollowingComma(string file, int startIndex) {
         return GetWholeBlockUntilFollowingCharacter(file, startIndex, '(', ')', ',', out _);
      }

      static string GetStructNameOfAnonymousStructVariable(string variableName, string surroundingStructName) {
         return $"{surroundingStructName}_{variableName}_STRUCT";
      }

      /// <summary>
      /// It says surroundingStructName but it actually whatever the surrounding structture is. could be union too
      /// </summary>
      static string GetUnionNameOfAnonymousUnionVariable(string variableName, string surroundingStructName) {
         return $"{surroundingStructName}_{variableName}_UNION";
      }

      /// <summary>
      /// Used for getting a name for anonymous structs that doesnt have a variable declaration.
      /// Starts with __ANONYMOUS__ thats how you can understand if the struct is anonymous or not.
      /// </summary>
      static string GetStructNameOfAnonymousStructNoVariable(string surroundingStructName, int iotaValue) {
         return $"__ANONYMOUS__{surroundingStructName}_{iotaValue}_STRUCT";
      }

      static string GetUnionNameOfAnonymousUnionNoVariable(string surroundingStructName, int iotaValue) {
         return $"__ANONYMOUS__{surroundingStructName}_{iotaValue}_UNION";
      }

      static string GetEnumNameOfAnonymousEnumVariable(string variableName, string surroundingStructName) {
         return $"__ANONYMOUS__{surroundingStructName}_{variableName}_ENUM";
      }

      static string GetDelegateNameOfFunctionPointer(string functionPointerName, string surroundingFunctionName) {
         // surroundingFunctionName i think this is only null when function pointer is typedefd
         if (surroundingFunctionName == null) { 
            return $"{functionPointerName}";
         } else {
            return $"{surroundingFunctionName}_{functionPointerName}_DELEGATE";
         }
      }

      /// <summary>
      /// Make the size string a bit better. get the tokens and multiply them. int foo[5][7] -> "[5][7]" -> "5 * 7" <br />
      /// The size string is the one you get from structMemberArrayRegex.Groups["size"]
      /// </summary>
      /// <param name="size"></param>
      /// <returns></returns>
      static string MakeBetterSizeString(in string size, Dictionary<string, string> typedefs) {
         // {
         //    Regex getEverythingBetweenBracketsRegex = new Regex(@"\[(?<interior>.*?)\]", RegexOptions.Singleline);
         //    MatchCollection matches = getEverythingBetweenBracketsRegex.Matches(size);
         //    StringBuilder sizeBuilder = new StringBuilder();
         //    for (int i = 0; i < matches.Count; i++) {
         //       Match match = matches[i];
         //       sizeBuilder.Append(match.Groups["interior"].Value);
         //       if (i < matches.Count - 1) {
         //          sizeBuilder.Append(" * ");
         //       }
         //    }
         // }

         // this version is written considering the case where between the brackets there might be sizeof(UserDefinedType)
         // FIXME: there is a problem if sizeof(_type_) if _type_ is a user defined type then it wont compile on c# but if _type_ is a builtin type then it is no problem.
         //        ClangSharp evaluates the result of sizeof(UserDefinedType) and writes the result.
         //        solution:
         //          create a dotnet project copy the structs query the sizeof the struct write it to standard output.
         //          and then write those struct name size information to a Dictionary<string, int>
         //        NOTE: if i do the thing above then it is also going to be easy to extract out anonymous unions
         //        TODO: do the solution above
         //              i got better solution. the const keyword in c means that the value assigned cant be changed but the const keyword in c# requires value to be compile time constant
         //              so to better replicate const keyword in c in c# i can use static readonly.
         //              update. une problemo. if it is fixed array then the size must be constant. so if you dont want to bother try setting it to pointer and see if it is interop friendly
         {
            // TODO: this algorithm has another problem. it applies ResolveTypedefsAndApplyFullConversion() function on every word but the type might more than one word. e.g. unsigned long int
            //       i think types mostly exists inside of sizeof() so instead of searching for every word search for sizeof() and apply resolvng only on them
            string result = Regex.Replace(size, @"\]\s*\[", ") * (").TrimStart('[').TrimEnd(']');
            result = result.Insert(0, "(");
            result += ')';
            Regex wordRegex = new Regex(@"\w+");
            for (; ; ) {
               bool changed = false;
               MatchCollection wordMatches = wordRegex.Matches(result);
               foreach (Match wordMatch in wordMatches) {
                  string word = wordMatch.Value;
                  string resolved = ResolveTypedefsAndApplyFullConversion(word, typedefs); // assuming ResolveTypedefsAndApplyFullConversion() doesnt change the string if the string is not a ctype
                  if (resolved != word) {
                     result = Regex.Replace(result, @$"\b{word}\b", resolved);
                     changed = true;
                  }
               }
               if (!changed) {
                  break;
               }
            }
            result = ConvertWhiteSpacesToSingleSpace(result);
            return result;
         }
         
         // {
         //    Regex wordRegex = new Regex(@"\w+");
         //    MatchCollection wordsInSizeMatches = wordRegex.Matches(size);
         //    StringBuilder sizeBuilder = new StringBuilder();
         //    for (int i = 0; i < wordsInSizeMatches.Count; i++) {
         //       Match wordMatch = wordsInSizeMatches[i];
         //       sizeBuilder.Append(wordMatch.Value);
         //       if (i < wordsInSizeMatches.Count - 1) {
         //          sizeBuilder.Append(" * ");
         //       }
         //    }
         //    return sizeBuilder.ToString();
         // }
      }

      static string RemoveConsts(in string s, out bool hasConst) {
         Regex constRegex = new Regex(@"\bconst\b");
         string result = s;
         hasConst = false;
         for (; ; ) {
            Match match = constRegex.Match(result);
            if (!match.Success) {
               break;
            }
            hasConst = true;
            result = result.Remove(match.Index, match.Length);
         }

         return result;
      }

      static string GetTypeStringOfFunctionParameterArray(FunctionParameterArrayData parameterArrayData, out int numberOfOpeningBraces) {
         numberOfOpeningBraces = parameterArrayData.arrayPart.Count(c => c == '[');
         return $"{parameterArrayData.type}{new string('*', numberOfOpeningBraces)}";
      }

      /// <summary>
      /// The parameters in the returned list is in the order they appear in the <paramref name="functionArgs"/> string.
      /// </summary>
      static List<IFunctionParameterData> ExtractOutParameterDatasAndResolveFunctionPointers(
                           string functionArgs,
                           string nameOfFunctionPointerWhoOwnesTheReturnedParameters,
                           Regex functionArgRegex,
                           Regex functionArgArrayRegex,
                           Regex functionArgFunctionPointerRegex,
                           Queue<(string name, string returnType, string args, string stars, Group arrayPart, Group variadicPart, string surroundingFunctionName)> functionPointerFrontier,
                           Iota iota) {
         // function pointers
         {
            for (; ; ) {
               Match functionPointerMatch = functionArgFunctionPointerRegex.Match(functionArgs);
               if (!functionPointerMatch.Success) {
                  break;
               }
               string functionPointerReturnType = functionPointerMatch.Groups["returnType"].Value.Trim();
               string functionPointerStars = functionPointerMatch.Groups["stars"].Value.Trim();
               Group functionPointerNameGroup = functionPointerMatch.Groups["parameterName"];
               Group functionPointerArrayPart = functionPointerMatch.Groups["arrayPart"];
               string functionPointerFunctionArgs = functionPointerMatch.Groups["args"].Value.Trim();
               Group functionPointerVariadicPart = functionPointerMatch.Groups["variadicPart"];

               string functionPointerVariableName;
               if (functionPointerNameGroup.Success) {
                  functionPointerVariableName = functionPointerNameGroup.Value;
               } else {
                  functionPointerVariableName = GetAnonymousParameterName(iota);
               }
               string delegateName = GetDelegateNameOfFunctionPointer(functionPointerVariableName, nameOfFunctionPointerWhoOwnesTheReturnedParameters);

               functionArgs = functionArgs.Remove(functionPointerMatch.Index, functionPointerMatch.Value.Length)
                                          .Insert(functionPointerMatch.Index, $"{delegateName} {functionPointerVariableName},");

               functionPointerFrontier.Enqueue((
                  delegateName,
                  functionPointerReturnType,
                  functionPointerFunctionArgs,
                  functionPointerStars,
                  functionPointerArrayPart,
                  functionPointerVariadicPart,
                  nameOfFunctionPointerWhoOwnesTheReturnedParameters
               ));
            }
         }

         List<(IFunctionParameterData parameterData, int matchIndex)> parameterDatas = new List<(IFunctionParameterData parameterData, int matchIndex)>();
         MatchCollection functionArgsMatches = functionArgRegex.Matches(functionArgs);
         MatchCollection functionArgArrayMatches = functionArgArrayRegex.Matches(functionArgs);
         foreach (MatchCollection matchCollection in new MatchCollection[] { functionArgsMatches, functionArgArrayMatches }) {
            foreach (Match functionArgMatch in matchCollection) {
               Group parameterNameGroup = functionArgMatch.Groups["parameterName"];
               string type = functionArgMatch.Groups["type"].Value;
               string arrayPart = functionArgMatch.Groups["arrayPart"].Value;
               type = type.Trim();
               // type = EnsureTypeIsNotAReservedKeyword(type);

               string parameterName;
               if (parameterNameGroup.Success) {
                  parameterName = parameterNameGroup.Value;
               } else {
                  parameterName = GetAnonymousParameterName(iota);
               }
               parameterName = EnsureVariableIsNotAReservedKeyword(parameterName);

               if (matchCollection == functionArgArrayMatches) {
                  parameterDatas.Add((new FunctionParameterArrayData() {
                     type = type,
                     name = parameterName,
                     arrayPart = arrayPart
                  }, functionArgMatch.Index));
               } else if (matchCollection == functionArgsMatches) {
                  parameterDatas.Add((new FunctionParameterData() {
                     type = type,
                     name = parameterName,
                  }, functionArgMatch.Index));
               } else {
                  throw new UnreachableException();
               }
            }
         }

         parameterDatas.Sort((a, b) => a.matchIndex.CompareTo(b.matchIndex));
         return parameterDatas.Select(p => p.parameterData).ToList();
      }

      static string GetAnonymousParameterName(Iota iota) {
         return $"__ANONYMOUS__PARAMETER_{iota.Get()}";
      }

      static string GetFunctionArgsAsString(List<IFunctionParameterData> parameters) {
         StringBuilder functionArgs = new StringBuilder();
         for (int i = 0; i < parameters.Count; i++) {
            if (parameters[i] is FunctionParameterData) {
               FunctionParameterData parameterData = parameters[i] as FunctionParameterData;
               functionArgs.Append($"{parameterData.type} {parameterData.name}");
            } else if (parameters[i] is FunctionParameterArrayData) {
               FunctionParameterArrayData parameterData = parameters[i] as FunctionParameterArrayData;
               string type = GetTypeStringOfFunctionParameterArray(parameterData, out _);
               functionArgs.Append($"{type} {parameterData.name}");
            } else {
               throw new UnreachableException();
            }

            if (i != parameters.Count - 1) {
               functionArgs.Append(", ");
            }
         }

         return functionArgs.ToString();
      }

      static HashSet<string> ExtractOutExposedFunctionsFromDynsymTable(List<DynsymTableEntry> table) {
         HashSet<string> result = new HashSet<string>();
         foreach (DynsymTableEntry entry in table) {
            if (entry.type == "FUNC" && entry.bind == "GLOBAL" && entry.ndx != "UND") {
               result.Add(entry.name);
            }
         }

         return result;
      }

      static List<EnumMember> GetEnumMembersWithExplicitValues(List<EnumMember> enumMembers) {
         List<EnumMember> result = new List<EnumMember>();
         for (int i = 0; i < enumMembers.Count; i++) {
            EnumMember enumMember = enumMembers[i];
            EnumMember newEnumMember;

            if (enumMember.value == null) {
               if (i == 0) {
                  newEnumMember = new EnumMember() {
                     identifier = enumMember.identifier,
                     value = "0"
                  };
               } else {
                  newEnumMember = new EnumMember() {
                     identifier = enumMember.identifier,
                     value = $"({enumMembers[i - 1].identifier}) + 1"
                  };
               }
            } else {
               newEnumMember = enumMember.Copy();
            }

            result.Add(newEnumMember);
         }

         return result;
      }

      static List<EnumMember> ExtractOutEnumMembers(in string membersString, in Regex enumMemberRegex) {
         List<EnumMember> enumMembers = new List<EnumMember>();
         MatchCollection enumMemberMatches = enumMemberRegex.Matches(membersString);
         foreach (Match match in enumMemberMatches) {
            string identifier = match.Groups["identifier"].Value;
            string value;
            if (match.Groups["value"].Success) {
               value = match.Groups["value"].Value;
            } else {
               value = null;
            }

            // i dont need to this since at the beginning of processing i convert csharp reserved keywords to valid identifiers.
            // when it comes to c keywords it cant be here if the provided input is a valid c code and i assume it is.
            // identifier = EnsureVariableIsNotAReservedKeyword(identifier);
            // TODO : EnsureNotAReservedKeyword() should also be applied to words in value.

            enumMembers.Add(new EnumMember() {
               identifier = identifier,
               value = value
            });
         }

         return enumMembers;
      }

      static string ConvertCGlobalConstVariableValueToCSharpValue(in string cvalue, Regex dotMemberEqualsRegex, Regex noNewBeforeCurlyBraceRegex, Regex openCurlyBraceRegex) {
         string result = $"{cvalue},";

         // convert array blocks into array initalizers and struct initializers in to new() {}
         {
            for (; ; ) {
               Match match = noNewBeforeCurlyBraceRegex.Match(result);
               if (!match.Success) {
                  break;
               }

               string block = GetWholeBlockUntilFollowingCharacter(result, match.Index, '{', '}', ',', out int indexOfBlockEnd);
               if (IsArrayBlock(block, openCurlyBraceRegex, dotMemberEqualsRegex)) {
                  // replace { ... } with [ ... ]
                  result = result.Remove(match.Index, 1)
                                 .Insert(match.Index, "[")
                                 .Remove(indexOfBlockEnd, 1)
                                 .Insert(indexOfBlockEnd, "]");
               } else {
                  result = result.Insert(match.Index, "new() ");
               }
            }
         }

         // remove dots from dot member equals syntax. e.g. { .x = 5 } -> { x = 5 }
         // TODO : if ".x = _value_" if _value_ is a csharp keyword then you need to apply EnsureNotAReservedKeyword() to it.
         //       not anymore. same reason as enums
         {
            for (; ; ) {
               Match dotMemberEqualsMatch = dotMemberEqualsRegex.Match(result);
               if (!dotMemberEqualsMatch.Success) {
                  break;
               }
               int indexOfDot = dotMemberEqualsMatch.Groups["dot"].Index;
               result = result.Remove(indexOfDot, 1);
            }
         }

         result = result.TrimEnd(',', ' ');
         result = result.Replace("\n", "\n\t\t");

         return result;
      }

      // TODO: this has a problem. if initilization of struct doesnt have .type = but relies on the order then i am fucked up.
      //       also ConvertCGlobalConstVariableValueToCSharpValue() wont generate correct output if thats the case
      static bool IsArrayBlock(in string block, Regex openCurlyBraceRegex, Regex dotMemberEqualsRegex) {
         // except for the outer most block remove every block
         string result = block;

         // remove outer most braces
         {
            result = result.Remove(result.IndexOf('{'), 1);
            result = result.Remove(result.LastIndexOf('}'), 1);
         }

         // remove blocks inside
         {
            for (; ; ) {
               Match curlyBraceMatch = openCurlyBraceRegex.Match(result);
               if (!curlyBraceMatch.Success) {
                  break;
               }
               string wholeBlock = GetWholeBlockUntilFollowingCharacter(result, curlyBraceMatch.Index, '{', '}', ',', out _);
               result = result.Remove(curlyBraceMatch.Index, wholeBlock.Length);
            }
         }

         // and after these steps if it matches dotMemberEqualsRegex then it is a struct block otherwise array block
         return !dotMemberEqualsRegex.IsMatch(result);
      }

      static List<string> ExtractOutArrayParts(in string arrayPart, Regex openSquareBracketRegex) {
         List<string> result = new List<string>();
         string target = arrayPart;

         for (; ; ) {
            Match match = openSquareBracketRegex.Match(target);
            if (!match.Success) {
               break;
            }

            string block = GetWholeBlockUntilFollowingCharacter(target, match.Index, '[', ']', null, out _);
            target = target.Remove(match.Index, block.Length);

            block = block.Remove(0, 1);
            block = block.Remove(block.Length - 1, 1);

            result.Add(block);
         }

         return result;
      }

      /// <summary>
      /// The returned string contains multiple variable declarations but they are still in C syntax
      /// </summary>
      static string GetSingleVariableDeclarationsFromCommaOperatorDeclaredVariablesStillInC(string type, string[] stars, string[] variableName, string[] arrayPart) {
         StringBuilder result = new StringBuilder();
         Debug.Assert(stars.Length == variableName.Length && variableName.Length == arrayPart.Length);
         for (int i = 0; i < stars.Length; i++) {
            result.AppendLine($"{type}{stars[i]} {variableName[i]}{arrayPart[i]};");
         }

         return result.ToString();
      }

      static string EnsureVariableIsNotAReservedKeyword(in string s) {
         return TypeInfo.csharpReservedKeywords.Contains(s) ? $"{s}_" : s;
      }

      [Obsolete("Switched to a different approach. new approach, at the beginning replace the keywords with something else")]
      static string EnsureTypeIsNotAReservedKeyword(in string s) {
         if (TypeInfo.csharpReservedKeywordsThatAreTypes.Contains(s) && !TypeInfo.cReservedKeywordsThatAreTypes.Contains(s)) {
            return $"{s}_";
         } else {
            return s;
         }
      }

      static bool DoesTypeExist(string type,
                              Dictionary<string, StructData> structDatas,
                              Dictionary<string, StructData> unionDatas,
                              Dictionary<string, EnumData> enumDatas,
                              Dictionary<string, FunctionPointerData> functionPointerDatas,
                              HashSet<string> opaqueStructs,
                              HashSet<string> opaqueUnions,
                              HashSet<string> opaqueEnums) {
         return structDatas.ContainsKey(type) ||
               unionDatas.ContainsKey(type) ||
               enumDatas.ContainsKey(type) ||
               functionPointerDatas.ContainsKey(type) ||
               opaqueStructs.Contains(type) ||
               opaqueUnions.Contains(type) ||
               opaqueEnums.Contains(type) ||
               TypeInfo.builtinCSharpTypes.Contains(type);

         // if (!structDatas.ContainsKey(type) && !unionDatas.ContainsKey(type) && !enumDatas.ContainsKey(type) && !functionPointerDatas.ContainsKey(type) && !TypeInfo.builtinCSharpTypes.Contains(type)) {
         //    return false;
         // } else {
         //    return true;
         // }
      }
   }
}
