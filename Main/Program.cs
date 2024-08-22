using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// TODO: union, comments, pointer types, arrays, enums, structs inside structs, anynomus structs
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
            Process readelfProcess = new Process();
            ProcessStartInfo readelfProcessStartInfo = new ProcessStartInfo("readelf", $"-W --dyn-syms \"{soFile}\"") {
               RedirectStandardOutput = true,
            };

            readelfProcess.StartInfo = readelfProcessStartInfo;
            readelfProcess.Start();
            StreamReader readelfProcessOutputReader = readelfProcess.StandardOutput;
            readelfProcess.WaitForExit();
            string readelfOutput = readelfProcessOutputReader.ReadToEnd();
            readelfProcessOutputReader.Close();

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
            foreach (string headerFile in headerFiles.Keys) {
               headerFiles[headerFile] = File.ReadAllText(headerFile);
            }
         }

         // Read preprocessed .h files
         {
            foreach (string preprocessedHeaderFile in preprocessedHeaderFiles.Keys) {
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
            foreach (string file in headerFiles.Values) {
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

         Dictionary<string /*function name*/, FunctionData> functionDatas = new Dictionary<string, FunctionData>();
         Dictionary<string /*struct name*/, StructData> structDatas = new Dictionary<string, StructData>();
         // Values stored here is already applied basicType conversion. long int -> int
         Dictionary<string /*new type*/, string /*what new type defined as*/> typedefs = new Dictionary<string, string>(); // basically keys and values are swapped compared to typedef syntax. typedef int newType; -> typedefs["newType"] = "int"
         {
            Regex functionRegex = new Regex(@"(?<returnType>\w+\s*\**)\s*(?<functionName>\w+)\s*\((?<args>[\w,\s*()\[\]]*?)\)\s*[{;]", RegexOptions.Singleline | RegexOptions.Multiline);
            Regex functionArgRegex = new Regex(@"(?<type>\w+(?:\s+\w+)?[\s*]\s*\**)\s*(?<parameterName>\w+)"); // type ends with either star or whitespace
            Regex structRegex = new Regex(@"struct\s+(?<name>\w+)\s*\{(?<fields>.*?)\}\s*;", RegexOptions.Singleline | RegexOptions.Multiline); // this regex stops early if struct contains complex members. structs inside of structs or unions.
            Regex greedyStructRegex = new Regex(@"struct\s+(?<name>\w+)\s*\{(?<fields>.*)\}\s*;", RegexOptions.Singleline | RegexOptions.Multiline);
            Regex typedefStructRegex = new Regex(@"typedef\s+struct(?:\s+\w+)?\s*\{(?<fields>.*?)\}\s*(?<name>\w+)\s*;", RegexOptions.Singleline | RegexOptions.Multiline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyTypedefStructRegex = new Regex(@"typedef\s+struct(?:\s+\w+)?\s*\{(?<fields>.*)\}\s*(?<name>\w+)\s*;", RegexOptions.Singleline | RegexOptions.Multiline);
            Regex structMemberRegex = new Regex(@"(?<type>\w+[\s*]\s*\**)\s*(?<name>\w+)\s*;"); // very similar to functionArgRegex
            Regex anonymousStructRegex = new Regex(@"struct\s*\{(?<fields>.*?)\}\s*;", RegexOptions.Singleline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyAnonymousStructRegex = new Regex(@"struct\s*\{(?<fields>.*)\}\s*;", RegexOptions.Singleline);
            Regex anonymousStructWithVariableDeclarationRegex = new Regex(@"struct\s*\{(?<fields>.*?)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline); // this regex stops early if struct contains complex members. structs inside of structs or unions. use GetWholeStruct() function
            Regex greedyAnonymousStructWithVariableDeclarationRegex = new Regex(@"struct\s*\{(?<fields>.*)\}\s*(?<variableName>\w+)\s*;", RegexOptions.Singleline);
            Regex typedefRegex = new Regex(@"typedef\s+(?<originalType>[\w\s]+?)\s+(?<newType>\w+)\s*;");
            Regex structMemberArrayRegex = new Regex(@"(?<type>\w+[\s*]\s*\**)\s*(?<name>\w+)\s*\[(?<size>[\w\[\]\s]+?)\]\s*;"); // size contains everything between the brackets. int foo[5][7] -> size = "5][7"
            foreach (string file in preprocessedHeaderFiles.Values) {
               // typedefs
               {
                  MatchCollection matches = typedefRegex.Matches(file);
                  foreach (Match match in matches) {
                     string originalType = match.Groups["originalType"].Value.Trim();
                     string newType = match.Groups["newType"].Value;
                     string csharpType = TypeInfo.basicTypes.TryGetValue(originalType, out string convertedType) ? convertedType : originalType;
                     if (csharpType.StartsWith("signed")) {
                        csharpType = csharpType.Replace("signed ", "");
                     }
                     if (!typedefs.TryAdd(newType, csharpType)) {
                        // Console.WriteLine($"Warning: typedefs dictionary already includes \"{newType}\". Value: \"{typedefs[newType]}\". you tried to set it to \"{csharpType}\"");
                     }
                  }
               }
            }

            foreach (string file in preprocessedHeaderFiles.Values) {
               // functions
               {
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

               // structs
               {
                  MatchCollection typedefStructMatches = typedefStructRegex.Matches(file);
                  MatchCollection structMatches = structRegex.Matches(file);
                  Queue<(string name, string fields)> frontier = new Queue<(string name, string fields)>();

                  foreach (MatchCollection matchCollection in new MatchCollection[] { typedefStructMatches, structMatches }) {
                     foreach (Match incompleteMatch in matchCollection) {
                        string wholeStruct = GetWholeStruct(file, incompleteMatch.Index);
                        Match match = matchCollection == typedefStructMatches ? greedyTypedefStructRegex.Match(wholeStruct) : greedyStructRegex.Match(wholeStruct);

                        string structName = match.Groups["name"].Value;
                        string fields = match.Groups["fields"].Value;
                        frontier.Enqueue((structName, fields));
                     }
                  }

                  while (frontier.Count > 0) {
                     (string structName, string fields) = frontier.Dequeue();
                     fields = fields.Trim();

                     // "fields" may contain complex members. structs inside of structs or unions.

                     // remove all the anonymous struct with variable declarations from the struct. and add them to frontier so the anonymous structs can be processed the same way with other structs
                     {
                        for (; ; ) {
                           Match anonymousStructWithVariableDeclarationMatchIncomplete = anonymousStructWithVariableDeclarationRegex.Match(fields);
                           if (!anonymousStructWithVariableDeclarationMatchIncomplete.Success) {
                              break;
                           }
                           string anonymousStructWithVariableDeclarationWholeStruct = GetWholeStruct(fields, anonymousStructWithVariableDeclarationMatchIncomplete.Index);
                           Match anonymousStructWithVariableDeclarationMatch = greedyAnonymousStructWithVariableDeclarationRegex.Match(anonymousStructWithVariableDeclarationWholeStruct);
                           string variableName = anonymousStructWithVariableDeclarationMatch.Groups["variableName"].Value;
                           string structNameOfAnonymousStructVariable = GetStructNameOfAnonymousStructVariable(variableName, structName);
                           fields = fields.Remove(anonymousStructWithVariableDeclarationMatchIncomplete.Index, anonymousStructWithVariableDeclarationWholeStruct.Length)
                                          .Insert(anonymousStructWithVariableDeclarationMatchIncomplete.Index, $"{structNameOfAnonymousStructVariable} {variableName};");

                           frontier.Enqueue((structNameOfAnonymousStructVariable, anonymousStructWithVariableDeclarationMatch.Groups["fields"].Value));
                        }
                     }

                     // anonymous structs with no variable declaration
                     {
                        for (; ; ) {
                           Match anonymousStructMatchIncomplete = anonymousStructRegex.Match(fields);
                           if (!anonymousStructMatchIncomplete.Success) {
                              break;
                           }
                           string anonymousStructWholeStruct = GetWholeStruct(fields, anonymousStructMatchIncomplete.Index);
                           Match anonymousStructMatch = greedyAnonymousStructRegex.Match(anonymousStructWholeStruct);
                           int iotaValueOfVariable = iota.Get();
                           string structNameOfAnonymousStruct = GetStructNameOfAnonymousStructNoVariable(structName, iotaValueOfVariable);
                           fields = fields.Remove(anonymousStructMatchIncomplete.Index, anonymousStructWholeStruct.Length)
                                          .Insert(anonymousStructMatchIncomplete.Index, $"{structNameOfAnonymousStruct} __ANONYMOUS__{iotaValueOfVariable};");

                           frontier.Enqueue((structNameOfAnonymousStruct, anonymousStructMatch.Groups["fields"].Value));
                        }
                     }

                     List<(IStructMember member, int matchIndex)> structMembers = new List<(IStructMember, int)>(); // matchIndex is used for sorting the members in the order they appear in the file
                     MatchCollection structMemberMatches = structMemberRegex.Matches(fields);
                     foreach (Match structMemberMatch in structMemberMatches) {
                        string name = structMemberMatch.Groups["name"].Value;
                        string type = structMemberMatch.Groups["type"].Value;
                        type = GetClearerTypeString(type);
                        type = ResolveTypedefsAndApplyBasicConversion(type, typedefs);

                        structMembers.Add((new StructMember() {
                           name = name,
                           type = type
                        }, structMemberMatch.Index));
                     }
                     
                     MatchCollection structMemberArrayMatches = structMemberArrayRegex.Matches(fields);
                     foreach (Match structMemberArrayMatch in structMemberArrayMatches) {
                        string name = structMemberArrayMatch.Groups["name"].Value;
                        string type = structMemberArrayMatch.Groups["type"].Value;
                        string size = structMemberArrayMatch.Groups["size"].Value;
                        type = GetClearerTypeString(type);
                        type = ResolveTypedefsAndApplyBasicConversion(type, typedefs);

                        // make the size string a bit better. get the tokens and multiply them. int foo[5][7] -> "5][7" -> "5 * 7"
                        {
                           Regex wordRegex = new Regex(@"\w+?");
                           MatchCollection wordsInSizeMatches = wordRegex.Matches(size);
                           StringBuilder sizeBuilder = new StringBuilder();
                           for (int i = 0; i < wordsInSizeMatches.Count; i++) {
                              Match wordMatch = wordsInSizeMatches[i];
                              sizeBuilder.Append(wordMatch.Value);
                              if (i < wordsInSizeMatches.Count - 1) {
                                 sizeBuilder.Append(" * ");
                              }
                           }
                           size = sizeBuilder.ToString();
                        }

                        structMembers.Add((new StructMemberArray() {
                           name = name,
                           type = type,
                           size = size
                        }, structMemberArrayMatch.Index));
                     }

                     structMembers.Sort((a, b) => a.matchIndex.CompareTo(b.matchIndex));
                     structDatas.TryAdd(structName, new StructData() {
                        name = structName,
                        fields = structMembers.Select(t => t.member).ToList()
                     });
                  }
               }
            }
         }

         string libName = Regex.Match(soFile, @".*?(lib)?(?<name>\w+).*?\.so").Groups["name"].Value;
         StringBuilder csOutput = new StringBuilder();
         csOutput.AppendLine($"/**");
         csOutput.AppendLine($" * This file is auto generated by ctocs");
         csOutput.AppendLine($" * github.com/apilatosba/ctocs");
         csOutput.AppendLine($"**/");
         csOutput.AppendLine($"using System;");
         csOutput.AppendLine($"using System.Runtime.InteropServices;");
         csOutput.AppendLine();
         csOutput.AppendLine($"namespace {libName} {{");
         csOutput.AppendLine($"  public static unsafe partial class Native {{");
         csOutput.AppendLine($"     public const string LIBRARY_NAME = @\"{soFile}\";");

         // defines
         {
            foreach (var kvp in singleLineDefines) {
               if (int.TryParse(kvp.Value, out int intValue)) {
                  csOutput.AppendLine($"     public const int {kvp.Key} = {intValue};");
                  singleLineDefineTypes.Add(kvp.Key, typeof(int));
               } else if (float.TryParse(kvp.Value, out float floatValue)) {
                  csOutput.AppendLine($"     public const float {kvp.Key} = {floatValue}f;");
                  singleLineDefineTypes.Add(kvp.Key, typeof(float));
               } else if (double.TryParse(kvp.Value, out double doubleValue)) {
                  csOutput.AppendLine($"     public const double {kvp.Key} = {doubleValue}d;");
                  singleLineDefineTypes.Add(kvp.Key, typeof(double));
               } else if (kvp.Value.StartsWith('"') && kvp.Value.EndsWith('"')) {
                  csOutput.AppendLine($"     public const string {kvp.Key} = {kvp.Value};");
                  singleLineDefineTypes.Add(kvp.Key, typeof(string));
               } else if (kvp.Value.StartsWith('\'') && kvp.Value.EndsWith('\'')) {
                  csOutput.AppendLine($"     public const char {kvp.Key} = {kvp.Value};");
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
                        csOutput.AppendLine($"     public const float {kvp.Key} = {floatValue2}f;");
                        singleLineDefineTypes.Add(kvp.Key, typeof(float));
                     }
                  } else if (suffix == "ll") {
                     if (long.TryParse(suffixRemoved.ToString(), out long longValue)) {
                        csOutput.AppendLine($"     public const long {kvp.Key} = {longValue};");
                        singleLineDefineTypes.Add(kvp.Key, typeof(long));
                     }
                  } else if (suffix == "ull" || suffix == "llu" || suffix == "ul" || suffix == "lu") {
                     if (ulong.TryParse(suffixRemoved.ToString(), out ulong ulongValue)) {
                        csOutput.AppendLine($"     public const ulong {kvp.Key} = {ulongValue};");
                        singleLineDefineTypes.Add(kvp.Key, typeof(ulong));
                     }
                  } else if (suffix == "l") {
                     // it could be anything. if it contains a dot then lets do double otherwise do long.
                     if (suffixRemoved.ToString().Contains('.')) {
                        if (double.TryParse(suffixRemoved.ToString(), out double doubleValue2)) {
                           csOutput.AppendLine($"     public const double {kvp.Key} = {doubleValue2}d;");
                           singleLineDefineTypes.Add(kvp.Key, typeof(double));
                        }
                     } else {
                        if (long.TryParse(suffixRemoved.ToString(), out long longValue2)) {
                           csOutput.AppendLine($"     public const long {kvp.Key} = {longValue2};");
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
                     typeOfKnownWord = singleLineDefineTypes[word.Value];
                  }
               }
               if (!anUnknownWordExists) {
                  csOutput.AppendLine($"     public const {typeOfKnownWord.FullName} {kvp.Key} = {kvp.Value};");
               }
            }
         }

         List<FunctionData> functionsInNative = new List<FunctionData>();
         // functions
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
                  csOutput.AppendLine($"      [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = \"{entry.name}\")]");
                  csOutput.AppendLine($"      public static extern {functionData.returnType} {functionData.name}({functionArgs});");
                  functionsInNative.Add(functionData);
               }
            }
         }

         csOutput.AppendLine("   }");

         // structs
         foreach (StructData structData in structDatas.Values) {
            if (structData.fields.Count == 0) {
               continue; // assuming the original struct is not empty and my regex matched it wrong. so skip
            }

            // csOutput.AppendLine($"  [StructLayout(LayoutKind.Explicit)]");
            csOutput.AppendLine($"  public unsafe partial struct {structData.name} {{");
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
                  if (member.type.StartsWith("__ANONYMOUS__")) {
                     StructData anonymousStructData = structDatas[member.type];
                     foreach (StructMember anonymousStructMember in anonymousStructData.fields) {
                        csOutput.AppendLine($"     public {anonymousStructMember.type} {anonymousStructMember.name};");
                     }
                  } else {
                     csOutput.AppendLine($"     public {member.type} {member.name};");
                  }
               } else if (structData.fields[i] is StructMemberArray) {
                  StructMemberArray memberArray = structData.fields[i] as StructMemberArray;
                  if (TypeInfo.allowedFixedSizeBufferTypes.Contains(memberArray.type)) {
                     csOutput.AppendLine($"     public fixed {memberArray.type} {memberArray.name}[{memberArray.size}];");
                  } else {
                     // TODO: idk what to do
                  }
               }
            }
            csOutput.AppendLine("   }");
         }

         // Safe wrapper
         csOutput.AppendLine("   public static unsafe partial class Safe {");

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

               csOutput.AppendLine($"     public static {functionData.returnType} {functionData.name}({string.Join(", ", newParameters.Select(p => $"{p.type} {p.name}"))}) {{");
               foreach (int index in indicesOfParametersToBeModified) {
                  FunctionParameterData parameterData = functionData.parameters[index];
                  csOutput.AppendLine($"        fixed({parameterData.type} {parameterData.name}Ptr = &{parameterData.name}[0])");
               }
               csOutput.AppendLine($"        {(functionData.returnType == "void" ? "" : "return ")}Native.{functionData.name}({string.Join(", ", newParameters.Select((p, i) => indicesOfParametersToBeModified.Contains(i) ? $"{p.name}Ptr" : p.name))});");
               csOutput.AppendLine("     }");
            }
         }
         csOutput.AppendLine("   }");

         csOutput.AppendLine("}");

         string outputDirectory = $"ctocs_{libName}";
         Directory.CreateDirectory(outputDirectory);
         File.WriteAllText(Path.Combine(outputDirectory, $"{libName}.cs"), csOutput.ToString());
         Process.Start("dotnet", $"format whitespace --folder {outputDirectory}").WaitForExit();
      }

      static void PrintHelp() {
         string help =
         """
         Usage: ctocs sofile <.so file> hfiles <list of .h files>,, phfiles <list of preprocessed .h file>,,
                ctocs [--help | -h]

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
            "int", "long", "short", "char", "float", "double", "byte", "bool"
         }.Contains(type);
      }

      /// <summary>
      /// Uses curly brace count. The string returned starts from file[startIndex] and ends at the first semicolon after the last closing curly brace.
      /// </summary>
      static string GetWholeStruct(string file, int startIndex) {
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
         return $"{surroundingStructName}_{variableName}_struct";
      }

      /// <summary>
      /// Used for getting a name for anonymous structs that doesnt have a variable declaration.
      /// Starts with __ANONYMOUS__ thats how you can understand if the struct is anonymous or not.
      /// </summary>
      static string GetStructNameOfAnonymousStructNoVariable(string surroundingStructName, int iotaValue) {
         return $"__ANONYMOUS__{surroundingStructName}_{iotaValue}_struct";
      }
   }
}
