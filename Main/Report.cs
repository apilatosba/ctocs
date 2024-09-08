using System.Collections.Generic;

namespace Main {
   public class Report {
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
      public HashSet<string> removedStructsBecauseTheyDontHaveAnyFields;
      public HashSet<string> removedUnionsBecauseTheyDontHaveAnyFields;
      public HashSet<string> removedEnumsBecauseTheyDontHaveAnyIdentifier;
      public HashSet<string> incompleteStructsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers;
      public HashSet<string> incompleteUnionsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers;

      public Report() {
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
         removedStructsBecauseTheyDontHaveAnyFields = new HashSet<string>();
         incompleteStructsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers = new HashSet<string>();
         incompleteUnionsBecauseTheyContainAnArrayWithATypeNotAllowedInFixedSizeBuffers = new HashSet<string>();
         removedUnionsBecauseTheyDontHaveAnyFields = new HashSet<string>();
         removedEnumsBecauseTheyDontHaveAnyIdentifier = new HashSet<string>();
      }
   }
}