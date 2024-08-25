using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// TODO: comments, function pointers.
//       anonymous unions and anonymous structs access syntax.
//       safe wrapper for pointer types
//       nested types
//       in csOutput switch to \t instead of manual spacing
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
            if (showProgress) {
               Console.WriteLine("Reading preprocessed .h files...");
            }

            foreach (string preprocessedHeaderFile in preprocessedHeaderFiles.Keys) {
               if (showProgress) {
                  Console.WriteLine($"\tReading {preprocessedHeaderFile}...");
               }
               
               preprocessedHeaderFiles[preprocessedHeaderFile] = File.ReadAllText(preprocessedHeaderFile);
            }
         }

         Dictionary<string, Type> singleLineDefineTypes = new Dictionary<string, Type>(); // includes the type of the entry in singleLineDefines if it is written to csOutput. #define FOO 5 -> singleLineDefineTypes["FOO"] = typeof(int)
         Dictionary<string, string> singleLineDefines = new Dictionary<string, string>(); // only the ones that can be written as "const ... = ..." are added here. #define FOO 5 -> defines["FOO"] = "5"
         Dictionary<string, string> defines = new Dictionary<string, string>();
         {
            // TODO: single line define should also support this kinda thing. #define FOO 1 << 8. #define FOO (1 << 8)
            Regex singleLineDefineRegex = new Regex(@"^ *# *define +(?<name>\w+) +(?<value>[""']?[\w.]+[""']?) *$", RegexOptions.Multiline);
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
                     string value = match.Groups["value"].Value;
                     if (!singleLineDefines.TryAdd(name, value)) {
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
                     defines.TryAdd(name, value);
                  }
               }
            }
         }

         // used for generating unique names for anonymous structs that has no variable declaration. both for anonymous structs and thier generated variables. __ANONYMOUS__<iota>, __ANONYMOUS__<surroundingStructName>_<iota>_struct
         Iota iota = new Iota();

         HashSet<string /*union name*/> unionsThatArePresentInOriginalCCode = new HashSet<string>();
         HashSet<string /*struct name*/> structsThatArePresentInOriginalCCode = new HashSet<string>(); // only the structs that are present in the original C code. not the ones that are generated by this program.
         Dictionary<string /*function name*/, FunctionData> functionDatas = new Dictionary<string, FunctionData>();
         Dictionary<string /*struct name*/, StructData> structDatas = new Dictionary<string, StructData>(); // contains all the structs that need to processed and need to be written to csOutput. including the ones that are generated by this program
         // uses StructData because i didnt want to rename it to StructOrUnionData.
         Dictionary<string /*union name*/, StructData> unionDatas = new Dictionary<string, StructData>(); // contains all the unions that need to processed and need to be written to csOutput. including the ones that are generated by this program
         // Values stored here is already applied basicType conversion. long int -> int
         Dictionary<string /*new type*/, string /*what new type defined as*/> typedefs = new Dictionary<string, string>(); // basically keys and values are swapped compared to typedef syntax. typedef int newType; -> typedefs["newType"] = "int"
         Dictionary<string /*enum name*/, EnumData> enumDatas = new Dictionary<string, EnumData>();
         List<List<EnumMember>> anonymousEnums = new List<List<EnumMember>>();
         {
            Regex functionRegex = new Regex(@"(?<returnType>\w+\s*\**)\s*(?<functionName>\w+)\s*\((?<args>[\w,\s*()\[\]]*?)\)\s*[{;]", RegexOptions.Singleline | RegexOptions.Multiline);
            Regex functionArgRegex = new Regex(@"(?<type>\w+(?:\s+\w+)?[\s*]\s*\**)\s*(?<parameterName>\w+)"); // type ends with either star or whitespace
            Regex structRegex = new Regex(@"struct\s+(?<name>\w+)\s*\{(?<fields>.*?)\}\s*;", RegexOptions.Singleline | RegexOptions.Multiline); // this regex stops early if struct contains complex members. structs inside of structs or unions.
            Regex greedyStructRegex = new Regex(@"struct\s+(?<name>\w+)\s*\{(?<fields>.*)\}\s*;", RegexOptions.Singleline | RegexOptions.Multiline);
            // TODO: typedefStructRegex doesnt capture the name after the keyword "struct". it skips it and only uses the name at the end of the struct. this problem also exist in greedy version and union version. should i handle this above in typedefs?
            Regex typedefStructRegex = new Regex(@"typedef\s+struct(?:\s+\w+)?\s*\{(?<fields>.*?)\}\s*(?<name>\w+)\s*;", RegexOptions.Singleline | RegexOptions.Multiline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyTypedefStructRegex = new Regex(@"typedef\s+struct(?:\s+\w+)?\s*\{(?<fields>.*)\}\s*(?<name>\w+)\s*;", RegexOptions.Singleline | RegexOptions.Multiline);
            Regex structMemberRegex = new Regex(@"(?<type>\w+[\s*]\s*\**)\s*(?<name>\w+)\s*;"); // very similar to functionArgRegex
            Regex anonymousStructRegex = new Regex(@"struct\s*\{(?<fields>.*?)\}\s*;", RegexOptions.Singleline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyAnonymousStructRegex = new Regex(@"struct\s*\{(?<fields>.*)\}\s*;", RegexOptions.Singleline);
            Regex anonymousUnionRegex = new Regex(@"union\s*\{(?<fields>.*?)\}\s*;", RegexOptions.Singleline); // this regex stops early if union contains complex members. unions inside of unions or structs. use GetWholeStruct() function
            Regex greedyAnonymousUnionRegex = new Regex(@"union\s*\{(?<fields>.*)\}\s*;", RegexOptions.Singleline);
            Regex anonymousStructWithVariableDeclarationRegex = new Regex(@"struct\s*\{(?<fields>.*?)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyAnonymousStructWithVariableDeclarationRegex = new Regex(@"struct\s*\{(?<fields>.*)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline);
            Regex anonymousUnionWithVariableDeclarationRegex = new Regex(@"union\s*\{(?<fields>.*?)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyAnonymousUnionWithVariableDeclarationRegex = new Regex(@"union\s*\{(?<fields>.*)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline);
            Regex typedefRegex = new Regex(@"typedef\s+(?<originalType>[\w\s]+?)\s+(?<newType>\w+)\s*;");
            Regex structMemberArrayRegex = new Regex(@"(?<type>\w+[\s*]\s*\**)\s*(?<name>\w+)\s*\[(?<size>[\w\[\]\s]+?)\]\s*;"); // size contains everything between the brackets. int foo[5][7] -> size = "5][7"
            Regex unionRegex = new Regex(@"union\s+(?<name>\w+)\s*\{(?<fields>.*?)\}\s*;", RegexOptions.Singleline); // this regex stops early if union contains complex members. unions inside of unions or structs.
            Regex greedyUnionRegex = new Regex(@"union\s+(?<name>\w+)\s*\{(?<fields>.*)\}\s*;", RegexOptions.Singleline);
            Regex typedefUnionRegex = new Regex(@"typedef\s+union(?:\s+\w+)?\s*\{(?<fields>.*?)\}\s*(?<name>\w+)\s*;", RegexOptions.Singleline);
            Regex greedyTypedefUnionRegex = new Regex(@"typedef\s+union(?:\s+\w+)?\s*\{(?<fields>.*)\}\s*(?<name>\w+)\s*;", RegexOptions.Singleline);
            Regex enumRegex = new Regex(@"enum\s+(?<name>\w+)\s*\{(?<members>.*?)\}\s*;", RegexOptions.Singleline);
            Regex typedefEnumRegex = new Regex(@"typedef\s+enum(?:\s+\w+)?\s*\{(?<members>.*?)\}\s*(?<name>\w+)\s*;", RegexOptions.Singleline);
            Regex enumMemberRegex = new Regex(@"(?<identifier>\w+)(?:\s*=\s*(?<value>[\w\s'()+\-*\/&|%<>!\^~]+?))?\s*,");
            Regex anonymousEnumRegex = new Regex(@"enum\s*\{(?<members>.*?)\}\s*;", RegexOptions.Singleline);
            Regex anonymousEnumWithVariableDeclarationRegex = new Regex(@"enum\s*\{(?<members>.*?)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline);
            foreach (var kvp in preprocessedHeaderFiles) {
               string file = kvp.Value; // file contents
               string path = kvp.Key;

               // typedefs
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing typedefs [{path}]...");
                  }
                  
                  MatchCollection matches = typedefRegex.Matches(file);
                  foreach (Match match in matches) {
                     string originalType = match.Groups["originalType"].Value.Trim();
                     string newType = match.Groups["newType"].Value;
                     string csharpType = TypeInfo.basicTypes.TryGetValue(originalType, out string convertedType) ? convertedType : originalType;
                     if (csharpType.StartsWith("signed")) {
                        csharpType = csharpType.Remove(0, "signed ".Length).Trim();
                     }
                     if (!typedefs.TryAdd(newType, csharpType)) {
                        // Console.WriteLine($"Warning: typedefs dictionary already includes \"{newType}\". Value: \"{typedefs[newType]}\". you tried to set it to \"{csharpType}\"");
                     }
                  }
               }
            }

            foreach (var kvp in preprocessedHeaderFiles) {
               string file = kvp.Value; // file contents
               string path = kvp.Key;
               
               Queue<(string name, string fields)> unionFrontier = new Queue<(string name, string fields)>();
               Queue<(string name, string fields)> structFrontier = new Queue<(string name, string fields)>();
               Queue<(string enumName, string members)> enumFrontier = new Queue<(string enumName, string members)>();

               // functions
               {
                  if (showProgress) {
                     Console.WriteLine($"Processing functions [{path}]...");
                  }
                  
                  MatchCollection matches = functionRegex.Matches(file);
                  foreach (Match match in matches) {
                     string returnType = match.Groups["returnType"].Value.Replace(" ", ""); // get rid of the spaces including this type of thing "int *"
                     string functionName = match.Groups["functionName"].Value.Trim();
                     string functionArgs = match.Groups["args"].Value.Trim();

                     returnType = ResolveTypedefsAndApplyBasicConversion(returnType, typedefs);

                     List<FunctionParameterData> parameterDatas = new List<FunctionParameterData>();
                     string[] functionArgsSplitted = functionArgs.Split(',');
                     foreach (string elementInSplitted in functionArgsSplitted) {
                        Match functionArgMatch = functionArgRegex.Match(elementInSplitted);
                        string parameterName = functionArgMatch.Groups["parameterName"].Value;
                        string type = functionArgMatch.Groups["type"].Value; // from "type" i want to get only the last token. i dont want to get "struct" from "struct foo". but i do want to keep the star if it is a pointer.
                        type = GetClearerTypeString(type);
                        type = ResolveTypedefsAndApplyBasicConversion(type, typedefs);

                        parameterDatas.Add(new FunctionParameterData() {
                           type = type,
                           name = parameterName
                        });
                     }

                     functionDatas.TryAdd(functionName, new FunctionData() {
                        returnType = returnType,
                        name = functionName,
                        parameters = parameterDatas,
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

                  foreach (MatchCollection matchCollection in new MatchCollection[] { typedefStructMatches, structMatches }) {
                     foreach (Match incompleteMatch in matchCollection) {
                        string wholeStruct = GetWholeBlockUntilFollowingSemicolon(file, incompleteMatch.Index);
                        Match match = matchCollection == typedefStructMatches ? greedyTypedefStructRegex.Match(wholeStruct) : greedyStructRegex.Match(wholeStruct);

                        string structName = match.Groups["name"].Value;
                        string fields = match.Groups["fields"].Value;
                        structsThatArePresentInOriginalCCode.Add(structName);
                        structFrontier.Enqueue((structName, fields));
                     }
                  }

                  // union
                  MatchCollection unionMatches = unionRegex.Matches(file);
                  MatchCollection typedefUnionMatches = typedefUnionRegex.Matches(file);

                  foreach (MatchCollection matchCollection in new MatchCollection[] { typedefUnionMatches, unionMatches }) {
                     foreach (Match incompleteMatch in matchCollection) {
                        string wholeUnion = GetWholeBlockUntilFollowingSemicolon(file, incompleteMatch.Index);
                        Match match = matchCollection == typedefUnionMatches ? greedyTypedefUnionRegex.Match(wholeUnion) : greedyUnionRegex.Match(wholeUnion);

                        string unionName = match.Groups["name"].Value;
                        string fields = match.Groups["fields"].Value;
                        unionsThatArePresentInOriginalCCode.Add(unionName);
                        unionFrontier.Enqueue((unionName, fields));
                     }
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
                           string anonymousStructWithVariableDeclarationWholeStruct = GetWholeBlockUntilFollowingSemicolon(fields, anonymousStructWithVariableDeclarationMatchIncomplete.Index);
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
                           string anonymousStructWholeStruct = GetWholeBlockUntilFollowingSemicolon(fields, anonymousStructMatchIncomplete.Index);
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
                           string anonymousUnionWholeUnion = GetWholeBlockUntilFollowingSemicolon(fields, anonymousUnionMatchIncomplete.Index);
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
                           string anonymousUnionWithVariableDeclarationWholeUnion = GetWholeBlockUntilFollowingSemicolon(fields, anonymousUnionWithVariableDeclarationMatchIncomplete.Index);
                           Match anonymousUnionWithVariableDeclarationMatch = greedyAnonymousUnionWithVariableDeclarationRegex.Match(anonymousUnionWithVariableDeclarationWholeUnion);
                           string variableName = anonymousUnionWithVariableDeclarationMatch.Groups["variableName"].Value;
                           string unionNameOfAnonymousUnionVariable = GetUnionNameOfAnonymousUnionVariable(variableName, structOrUnionName);
                           fields = fields.Remove(anonymousUnionWithVariableDeclarationMatchIncomplete.Index, anonymousUnionWithVariableDeclarationWholeUnion.Length)
                                          .Insert(anonymousUnionWithVariableDeclarationMatchIncomplete.Index, $"{unionNameOfAnonymousUnionVariable} {variableName};");

                           unionFrontier.Enqueue((unionNameOfAnonymousUnionVariable, anonymousUnionWithVariableDeclarationMatch.Groups["fields"].Value));
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
                        type = GetClearerTypeString(type);
                        type = ResolveTypedefsAndApplyBasicConversion(type, typedefs);

                        structOrUnionMembers.Add((new StructMember() {
                           name = name,
                           type = type
                        }, structMemberMatch.Index));
                     }

                     MatchCollection structOrUnionMemberArrayMatches = structMemberArrayRegex.Matches(fields);
                     foreach (Match structMemberArrayMatch in structOrUnionMemberArrayMatches) {
                        string name = structMemberArrayMatch.Groups["name"].Value;
                        string type = structMemberArrayMatch.Groups["type"].Value;
                        string size = structMemberArrayMatch.Groups["size"].Value;
                        type = GetClearerTypeString(type);
                        type = ResolveTypedefsAndApplyBasicConversion(type, typedefs);
                        size = MakeBetterSizeString(size);

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

                  foreach (MatchCollection matchCollection in new MatchCollection[] { enumMatches, typedefEnumMatches }) {
                     foreach (Match enumMatch in matchCollection) {
                        // i have this whole enum check because the regex might overshoot. i think only enumRegex can overshoot but to be safe i check both.
                        string wholeEnum = GetWholeBlockUntilFollowingSemicolon(file, enumMatch.Index);
                        Match wholeEnumMatch = matchCollection == typedefEnumMatches ? typedefEnumRegex.Match(wholeEnum) : enumRegex.Match(wholeEnum);
                        if (!wholeEnumMatch.Success) {
                           continue;
                        }

                        string enumName = wholeEnumMatch.Groups["name"].Value;
                        string membersString = wholeEnumMatch.Groups["members"].Value;
                        enumFrontier.Enqueue((enumName, membersString));
                     }
                  }

                  while (enumFrontier.Count > 0) {
                     (string enumName, string membersString) = enumFrontier.Dequeue();
                     membersString = membersString.Trim();

                     // remove the enum name if identifiers have them as prefix
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

                        enumMembers.Add(new EnumMember() {
                           identifier = identifier,
                           value = value
                        });
                     }

                     enumDatas.TryAdd(enumName, new EnumData() {
                        name = enumName,
                        members = enumMembers
                     });
                  }

                  // anonymous enums
                  {
                     MatchCollection anonymousEnumMatches = anonymousEnumRegex.Matches(file);
                     MatchCollection anonymousEnumWithVariableDeclarationMatches = anonymousEnumWithVariableDeclarationRegex.Matches(file);
                     foreach (MatchCollection matchCollection in new MatchCollection[] { anonymousEnumMatches, anonymousEnumWithVariableDeclarationMatches }) {
                        foreach (Match enumMatch in matchCollection) {
                           // i have this whole enum check because the regex might overshoot. i think only anonymousEnumRegex can overshoot but to be safe i check both.
                           string wholeEnum = GetWholeBlockUntilFollowingSemicolon(file, enumMatch.Index);
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
                                    value = $"{enumMembers[enumMembers.Count - 1].identifier} + 1";
                                 }
                              }

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
            }
         }

         string libName = Regex.Match(soFile, @".*?(lib)?(?<name>\w+)[\w.]*?\.so").Groups["name"].Value;
         StringBuilder csOutput = new StringBuilder();
         csOutput.AppendLine($"/**");
         csOutput.AppendLine($" * This file is auto generated by ctocs");
         csOutput.AppendLine($" * https://github.com/apilatosba/ctocs");
         csOutput.AppendLine($"**/");
         csOutput.AppendLine($"using System;");
         csOutput.AppendLine($"using System.Runtime.InteropServices;");
         csOutput.AppendLine();
         csOutput.AppendLine($"namespace {libName} {{");
         csOutput.AppendLine($"\tpublic static unsafe partial class Native {{");
         csOutput.AppendLine($"\t\tpublic const string LIBRARY_NAME = @\"{soFile}\";");

         // defines
         {
            if (showProgress) {
               Console.WriteLine($"\tWriting defines...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t\t// DEFINES");
            foreach (var kvp in singleLineDefines) {
               if (int.TryParse(kvp.Value, out int intValue)) {
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
                  }
               }
            }

            // if there are defines that are written in terms of other defines, then we can write them as well.
            foreach (var kvp in defines) {
               if (singleLineDefines.ContainsKey(kvp.Key)) {
                  continue;
               }

               Regex wordRegex = new Regex(@"\w+");
               MatchCollection words = wordRegex.Matches(kvp.Value);
               bool anUnknownWordExists = false;
               Type typeOfKnownWord = null;
               foreach (Match word in words) {
                  if (!singleLineDefines.ContainsKey(word.Value)) {
                     anUnknownWordExists = true;
                     break;
                  } else {
                     if (singleLineDefineTypes.TryGetValue(word.Value, out Type value)) {
                        typeOfKnownWord = value;
                     }
                  }
               }
               if (!anUnknownWordExists) {
                  csOutput.AppendLine($"\t\tpublic const {typeOfKnownWord.FullName} {kvp.Key} = {kvp.Value};");
               }
            }
         }

         // anonymous enums
         {
            if (showProgress) {
               Console.WriteLine($"\tWriting anonymous enums...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t\t// ANONYMOUS ENUMS");
            foreach (List<EnumMember> anonymousEnum in anonymousEnums) {
               foreach (EnumMember enumMember in anonymousEnum) {
                  csOutput.AppendLine($"\t\tpublic const int {enumMember.identifier} = {enumMember.value};");
               }
            }
         }

         List<FunctionData> functionsInNative = new List<FunctionData>();
         // functions
         {
            if (showProgress) {
               Console.WriteLine($"\tWriting functions...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t\t// FUNCTIONS");
            foreach (DynsymTableEntry entry in dynsymTable) {
               if (entry.type == "FUNC" &&
                  entry.bind == "GLOBAL" &&
                  entry.ndx != "UND") {
                  string functionName = entry.name;
                  if (functionDatas.TryGetValue(functionName, out FunctionData functionData)) {
                     StringBuilder functionArgs = new StringBuilder();
                     for (int i = 0; i < functionData.parameters.Count; i++) {
                        FunctionParameterData parameterData = functionData.parameters[i];
                        functionArgs.Append($"{parameterData.type} {parameterData.name}");
                        if (i != functionData.parameters.Count - 1) {
                           functionArgs.Append(", ");
                        }
                     }
                     csOutput.AppendLine($"\t\t[DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = \"{entry.name}\")]");
                     csOutput.AppendLine($"\t\tpublic static extern {functionData.returnType} {functionData.name}({functionArgs});");
                     functionsInNative.Add(functionData);
                  }
               }
            }
         }

         csOutput.AppendLine("\t}");

         // structs
         {
            if (showProgress) {
               Console.WriteLine($"\tWriting structs...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// STRUCTS");
            foreach (StructData structData in structDatas.Values) {
               if (structData.fields.Count == 0) {
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
                     if (TypeInfo.allowedFixedSizeBufferTypes.Contains(memberArray.type)) {
                        csOutput.AppendLine($"\t\tpublic fixed {memberArray.type} {memberArray.name}[{memberArray.size}];");
                     } else {
                        // TODO: idk what to do
                     }
                  }
               }
               csOutput.AppendLine("\t}");
            }
         }

         // unions
         {
            if (showProgress) {
               Console.WriteLine($"\tWriting unions...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// UNIONS");
            foreach (StructData unionData in unionDatas.Values) {
               if (unionData.fields.Count == 0) {
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
                        csOutput.AppendLine($"\t\t[FieldOffset(0)]");
                        csOutput.AppendLine($"\t\tpublic fixed {memberArray.type} {memberArray.name}[{memberArray.size}];");
                     } else {
                        // TODO: donk
                     }
                  }
               }
               csOutput.AppendLine("\t}");
            }
         }

         // enums
         {
            if (showProgress) {
               Console.WriteLine($"\tWriting enums...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine($"\t// ENUMS");
            foreach (EnumData enumData in enumDatas.Values) {
               if (enumData.members.Count == 0) {
                  continue; // assuming the original enum is not empty and my regex matched it wrong. so skip
               }

               csOutput.AppendLine($"\tpublic enum {enumData.name} {{");
               foreach (EnumMember enumMember in enumData.members) {
                  csOutput.AppendLine($"\t\t{enumMember.identifier}{(string.IsNullOrEmpty(enumMember.value) ? "" : $" = {enumMember.value}")},");
               }
               csOutput.AppendLine("\t}");
            }
         }

         // Safe wrapper
         {
            if (showProgress) {
               Console.WriteLine($"\tWriting safe wrapper...");
            }

            csOutput.AppendLine();
            csOutput.AppendLine("\tpublic static unsafe partial class Safe {");

            // function parameters with single star pointers
            {
               foreach (FunctionData functionData in functionsInNative) {
                  List<FunctionParameterData> newParameters = new List<FunctionParameterData>();
                  List<int> indicesOfParametersToBeModified = new List<int>();
                  for (int i = 0; i < functionData.parameters.Count; i++) {
                     FunctionParameterData parameterData = functionData.parameters[i];
                     if (parameterData.type.Count(c => c == '*') == 1 && IsBasicType(parameterData.type.Substring(0, parameterData.type.Length - 1))) {
                        newParameters.Add(new FunctionParameterData() {
                           type = $"{parameterData.type.Replace("*", "[]")}",
                           name = parameterData.name
                        });
                        indicesOfParametersToBeModified.Add(i);
                     } else {
                        newParameters.Add(parameterData);
                     }
                  }

                  if (indicesOfParametersToBeModified.Count == 0) {
                     continue;
                  }

                  csOutput.AppendLine($"\t\tpublic static {functionData.returnType} {functionData.name}({string.Join(", ", newParameters.Select(p => $"{p.type} {p.name}"))}) {{");
                  foreach (int index in indicesOfParametersToBeModified) {
                     FunctionParameterData parameterData = functionData.parameters[index];
                     csOutput.AppendLine($"\t\t\tfixed({parameterData.type} {parameterData.name}Ptr = &{parameterData.name}[0])");
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
            Console.WriteLine($"Writing csharp file...");
         }
         File.WriteAllText(Path.Combine(outputDirectory, $"{libName}.cs"), csOutput.ToString());

         // is it just me or dotnet format kinda sucks
         // if (showProgress) {
         //    Console.WriteLine($"Formatting the code...");
         // }
         // Process.Start("dotnet", $"format whitespace --folder {outputDirectory}").WaitForExit();
      }

      static void PrintHelp() {
         string help =
         """
         Usage: ctocs sofile <.so file> hfiles <list of .h files>,, phfiles <list of preprocessed .h files>,, [options]
                ctocs [--help | -h]

         Options:
            --help, -h: Show this help message.
            no-show-progress: Do not show progress bar.
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

      // TODO: this function also removes specifiers. long int -> int. problem
      /// <summary>
      /// Example:
      ///   "struct Person* " -> "Person*"
      ///   "int * "          -> "int*"
      /// </summary>
      static string GetClearerTypeString(in string type) {
         string result = type.Trim();
         StringBuilder resultReconstrutedReverse = new StringBuilder();
         bool seenCharOtherThanStarAndWhiteSpace = false;
         for (int i = result.Length - 1; i >= 0; i--) {
            if (result[i] == '*') {
               resultReconstrutedReverse.Append('*');
            } else if (char.IsWhiteSpace(result[i])) {
               if (seenCharOtherThanStarAndWhiteSpace) {
                  break;
               }
            } else {
               seenCharOtherThanStarAndWhiteSpace = true;
               resultReconstrutedReverse.Append(result[i]);
            }
         }

         result = ReverseString(resultReconstrutedReverse.ToString());
         result = result.Replace(" ", "");
         return result;
      }

      static string ResolveTypedefsAndApplyBasicConversion(string ctype, Dictionary<string, string> typedefs) {
         bool isPointer = ctype.TrimEnd().EndsWith('*');

         string result = isPointer ? RemoveStarsFromEnd(ctype) : ctype;
         for (; ; ) {
            if (!typedefs.TryGetValue(result, out string newType)) {
               break;
            }
            result = newType;
         }

         result = TypeInfo.basicTypes.TryGetValue(result, out string convertedType) ? convertedType : result;

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
      static bool IsBasicType(string type) {
         return new string[] {
            "int", "uint", "long", "ulong", "short", "ushort", "char", "float", "double", "byte", "sbyte", "bool"
         }.Contains(type);
      }

      /// <summary>
      /// Uses curly brace count. The string returned starts from file[startIndex] and ends at the first semicolon after the last closing curly brace.
      /// </summary>
      static string GetWholeBlockUntilFollowingSemicolon(string file, int startIndex) {
         bool enteredFirstBrace = false;
         int openBraceCount = 0;
         int i = startIndex;
         for (; i < file.Length; i++) {
            if (file[i] == '{') {
               enteredFirstBrace = true;
               openBraceCount++;
            } else if (file[i] == '}') {
               openBraceCount--;
            }

            if (openBraceCount == 0 && enteredFirstBrace) {
               break;
            }
         }

         for (; i < file.Length; i++) {
            if (file[i] == ';') {
               break;
            }
         }

         return file.Substring(startIndex, i - startIndex + 1);
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

      /// <summary>
      /// Make the size string a bit better. get the tokens and multiply them. int foo[5][7] -> "5][7" -> "5 * 7" <br />
      /// The size string is the one you get from structMemberArrayRegex.Groups["size"]
      /// </summary>
      /// <param name="size"></param>
      /// <returns></returns>
      static string MakeBetterSizeString(in string size) {
         Regex wordRegex = new Regex(@"\w+");
         MatchCollection wordsInSizeMatches = wordRegex.Matches(size);
         StringBuilder sizeBuilder = new StringBuilder();
         for (int i = 0; i < wordsInSizeMatches.Count; i++) {
            Match wordMatch = wordsInSizeMatches[i];
            sizeBuilder.Append(wordMatch.Value);
            if (i < wordsInSizeMatches.Count - 1) {
               sizeBuilder.Append(" * ");
            }
         }
         return sizeBuilder.ToString();
      }
   }
}
