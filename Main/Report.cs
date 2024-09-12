using System.Collections.Generic;

namespace Main {
   public class Report {
      public List<UnresolvedSizeofTypeInAStructData> unresolvedSizeofTypeInStruct;
      public List<UnresolvedSizeofTypeInAGlobalConstVariableData> unresolvedSizeofTypesInGlobalConstVariable;
      public HashSet<string> definesThatAreDefinedMoreThanOnce;
      public HashSet<string> typedefdFunctionsOrWronglyCapturedFunctionPointers;
      public HashSet<string> functionsThatExistInHeaderFileButNotExposedInSOFile;
      public HashSet<RemovedElementWithUnknownTypeData> removedStructsBecauseTheyContainUnknownTypes;
      public HashSet<RemovedElementWithUnknownTypeData> removedUnionsBecauseTheyContainUnknownTypes;
      public HashSet<RemovedElementWithUnknownTypeData> removedFunctionsBecauseTheyContainUnknownTypes;
      public HashSet<RemovedElementWithUnknownTypeData> removedDelegatesBecauseTheyContainUnknownTypes;
      public HashSet<RemovedElementWithUnknownTypeData> removedGlobalConstVariablesBecauseTheyContainUnknownTypes;
      public HashSet<RemovedElementWithUnknownTypeData> removedFixedBufferStructsBecauseTheyContainUnknownTypes;
      public HashSet<string> functionPointersWithAmountOfStarsDifferentThanOne;
      public Dictionary<string /*name*/, string /*raw value*/> unableToParseDefines;
      public HashSet<string> externFunctions;

      public Report() {
         unresolvedSizeofTypeInStruct = new List<UnresolvedSizeofTypeInAStructData>();
         unresolvedSizeofTypesInGlobalConstVariable = new List<UnresolvedSizeofTypeInAGlobalConstVariableData>();
         definesThatAreDefinedMoreThanOnce = new HashSet<string>();
         typedefdFunctionsOrWronglyCapturedFunctionPointers = new HashSet<string>();
         functionsThatExistInHeaderFileButNotExposedInSOFile = new HashSet<string>();
         removedStructsBecauseTheyContainUnknownTypes = new HashSet<RemovedElementWithUnknownTypeData>();
         removedUnionsBecauseTheyContainUnknownTypes = new HashSet<RemovedElementWithUnknownTypeData>();
         removedFunctionsBecauseTheyContainUnknownTypes = new HashSet<RemovedElementWithUnknownTypeData>();
         removedDelegatesBecauseTheyContainUnknownTypes = new HashSet<RemovedElementWithUnknownTypeData>();
         removedGlobalConstVariablesBecauseTheyContainUnknownTypes = new HashSet<RemovedElementWithUnknownTypeData>();
         removedFixedBufferStructsBecauseTheyContainUnknownTypes = new HashSet<RemovedElementWithUnknownTypeData>();
         functionPointersWithAmountOfStarsDifferentThanOne = new HashSet<string>();
         unableToParseDefines = new Dictionary<string, string>();
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

   public class RemovedElementWithUnknownTypeData {
      public string name;
      public string unknownType;
   }
}