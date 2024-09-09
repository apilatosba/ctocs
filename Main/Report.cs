using System.Collections.Generic;

namespace Main {
   public class Report {
      public List<UnresolvedSizeofTypeInAStructData> unresolvedSizeofTypeInStruct;
      public List<UnresolvedSizeofTypeInAGlobalConstVariableData> unresolvedSizeofTypesInGlobalConstVariable;
      public HashSet<string> definesThatAreDefinedMoreThanOnce;
      public HashSet<string> typedefdFunctionsOrWronglyCapturedFunctionPointers;
      public HashSet<string> functionsThatExistInHeaderFileButNotExposedInSOFile;
      public HashSet<string> removedStructsBecauseTheyContainUnknownTypes;
      public HashSet<string> removedUnionsBecauseTheyContainUnknownTypes;
      public HashSet<string> removedFunctionsBecauseTheyContainUnknownTypes;
      public HashSet<string> removedDelegatesBecauseTheyContainUnknownTypes;
      public HashSet<string> removedGlobalConstVariablesBecauseTheyContainUnknownTypes;
      public HashSet<string> functionPointersWithAmountOfStarsDifferentThanOne;
      public Dictionary<string /*name*/, string /*raw value*/> unableToParseDefines;
      public HashSet<string> incompleteStructsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers;
      public HashSet<string> incompleteUnionsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers;
      public HashSet<string> externFunctions;

      public Report() {
         unresolvedSizeofTypeInStruct = new List<UnresolvedSizeofTypeInAStructData>();
         unresolvedSizeofTypesInGlobalConstVariable = new List<UnresolvedSizeofTypeInAGlobalConstVariableData>();
         definesThatAreDefinedMoreThanOnce = new HashSet<string>();
         typedefdFunctionsOrWronglyCapturedFunctionPointers = new HashSet<string>();
         functionsThatExistInHeaderFileButNotExposedInSOFile = new HashSet<string>();
         removedStructsBecauseTheyContainUnknownTypes = new HashSet<string>();
         removedUnionsBecauseTheyContainUnknownTypes = new HashSet<string>();
         removedFunctionsBecauseTheyContainUnknownTypes = new HashSet<string>();
         removedDelegatesBecauseTheyContainUnknownTypes = new HashSet<string>();
         removedGlobalConstVariablesBecauseTheyContainUnknownTypes = new HashSet<string>();
         functionPointersWithAmountOfStarsDifferentThanOne = new HashSet<string>();
         unableToParseDefines = new Dictionary<string, string>();
         incompleteStructsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers = new HashSet<string>();
         incompleteUnionsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers = new HashSet<string>();
         externFunctions = new HashSet<string>();
      }
   }

   public class UnresolvedSizeofTypeInAStructData {
      public string structNameWhichContainsTheSizeof;
      public string nameOfTheField;
      public string unresolvedType;
   }

   public class UnresolvedSizeofTypeInAGlobalConstVariableData {
      public string nameOfTheVariable;
      public string unresolvedType;
   }
}