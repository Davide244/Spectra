; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
; The ids are frozen once they appear here: they travel in build logs and NoWarn
; lists, so a renumber changes somebody's build without anything being edited.

### New Rules

Rule ID | Category | Severity | Notes
--------|-----------------|----------|-------------------------------------------------------------
SPE001  | SpectraEntities | Error    | Entity class must be partial
SPE002  | SpectraEntities | Error    | Duplicate entity class name
SPE003  | SpectraEntities | Error    | Unsupported keyvalue member type
SPE004  | SpectraEntities | Error    | Entity input has the wrong signature
SPE005  | SpectraEntities | Error    | Reserved keyvalue name (targetname IS SceneNode.Name)
SPE006  | SpectraEntities | Error    | Keyvalue type does not match the member
SPE007  | SpectraEntities | Error    | Keyvalue member cannot be assigned
