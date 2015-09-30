﻿namespace DG.XrmDefinitelyTyped

open System.Xml.Linq
open System.Text.RegularExpressions

open Microsoft.Xrm.Sdk
open Microsoft.Xrm.Sdk.Metadata

open IntermediateRepresentation
open InterpretOptionSetMetadata
open Utility

module internal InterpretFormXml = 

  // IDs gotten from MSDN page: 
  //    https://msdn.microsoft.com/en-in/library/cc906186.aspx
  // Still need to identify some class ids found in certain Form XMLs
  let classIds = 
    [ ("B0C6723A-8503-4FD7-BB28-C8A06AC933C2", CheckBox)
      ("5B773807-9FB2-42DB-97C3-7A91EFF8ADFF", DateTime)
      ("C3EFE0C3-0EC6-42BE-8349-CBD9079DFD8E", Decimal)
      ("AA987274-CE4E-4271-A803-66164311A958", Duration)
      ("ADA2203E-B4CD-49BE-9DDF-234642B43B52", EmailAddress)
      ("6F3FB987-393B-4D2D-859F-9D0F0349B6AD", EmailBody)
      ("0D2C745A-E5A8-4C8F-BA63-C6D3BB604660", Float)
      ("FD2A7985-3187-444E-908D-6624B21F69C0", IFrame)
      ("C6D124CA-7EDA-4A60-AEA9-7FB8D318B68F", Integer)
      ("671A9387-CA5A-4D1E-8AB7-06E39DDCF6B5", Language)
      ("270BD3DB-D9AF-4782-9025-509E298DEC0A", Lookup)
      ("533B9E00-756B-4312-95A0-DC888637AC78", MoneyValue)
      ("06375649-C143-495E-A496-C962E5B4488E", Notes)
      ("CBFB742C-14E7-4A17-96BB-1A13F7F64AA2", PartyListLookup)
      ("3EF39988-22BB-4F0B-BBBE-64B5A3748AEE", Picklist)
      ("67FAC785-CD58-4F9F-ABB3-4B7DDC6ED5ED", RadioButtons)
      ("F3015350-44A2-4AA0-97B5-00166532B5E9", RegardingLookup)
      ("5D68B988-0661-4DB2-BC3E-17598AD3BE6C", StatusReason)
      ("E0DECE4B-6FC8-4A8F-A065-082708572369", TextArea)
      ("4273EDBD-AC1D-40D3-9FB2-095C621B552D", TextBox)
      ("1E1FC551-F7A8-43AF-AC34-A8DC35C7B6D4", TickerSymbol)
      ("7C624A0B-F59E-493D-9583-638D34759266", TimeZonePicklist)
      ("71716B6C-711E-476C-8AB8-5D11542BFB47", Url) 
      ("5C5600E0-1D6E-4205-A272-BE80DA87FD42", QuickView)
      ("62B0DF79-0464-470F-8AF7-4483CFEA0C7D", Map)
      ("E7A81278-8635-4D9E-8D4D-59480B391C5B", Subgrid)
      ("9C5CA0A1-AB4D-4781-BE7E-8DFBE867B87E", Timer)
    ] |> List.map (fun (id,t) -> id.ToUpper(), t) |> Map.ofList
    
  let getControl (controlId, _, controlClass) =
    let cType = 
      match controlClass with
      | DateTime -> ControlType.Date

      | Picklist 
      | StatusReason
      | RadioButtons
      | CheckBox  -> ControlType.OptionSet
        
      | Decimal 
      | Duration
      | Integer 
      | MoneyValue 
      | Float -> ControlType.Number

      | WebResource -> ControlType.WebResource
      | IFrame -> ControlType.IFrame 
        
      | Subgrid -> ControlType.SubGrid

      | PartyListLookup 
      | RegardingLookup 
      | Lookup -> ControlType.Lookup
        
      // TODO: Figure out if the following should be special control types
      | Language
      | QuickView
      | TimeZonePicklist 
      | TickerSymbol
      | Map
      | Timer
      | _ -> ControlType.Default

    controlId, cType

  let getAttribute (enums:Map<string,Type>) (_, attr, controlClass) =
    if attr = null then None else 

    let aType = 
      match controlClass with
      | Picklist 
      | StatusReason  -> AttributeType.OptionSet (enums.TryFind(attr) |? Type.Number)
      | RadioButtons 
      | CheckBox      -> AttributeType.OptionSet Type.Boolean
        
      | Decimal 
      | Duration
      | Integer 
      | MoneyValue 
      | Float         -> AttributeType.Number

      | PartyListLookup 
      | RegardingLookup 
      | Lookup        -> AttributeType.Lookup

      | EmailAddress 
      | EmailBody 
      | Notes
      | TextArea 
      | TextBox 
      | Url           -> AttributeType.Default Type.String

      | DateTime      -> AttributeType.Default Type.Date

      | _             -> AttributeType.Default Type.Any
        
    Some (attr, aType)

  
  let getValue (xEl:XElement) (str:string) =
    match xEl.Attribute(XName.Get(str)) with
    | null -> null
    | xattr -> xattr.Value

  let (|IsWebresouce|) (str:string) = str.StartsWith("WebResource_")
  let getControlClass id (classId:string) =
    match id with
      | IsWebresouce true -> ControlClassId.WebResource
      | _ -> 
        let normalizedClassId = Regex.Replace(classId.ToUpper(), "[{}]", "")
        match classIds.TryFind normalizedClassId with
        | Some x -> x
        | None -> ControlClassId.Other
  

  let renameControls (controls:XrmFormControl list) =
    controls
    |> List.groupBy (fun x -> fst x)
    |> List.map (fun (x,cs) -> 
      List.mapi (fun i (_,c) -> 
        if i > 0 then sprintf "%s%d" x i, c 
        else x, c
      ) cs)
    |> List.concat

  /// Function to interpret a single FormXml
  let interpretFormXml (enums:Map<string,Type>) (bpfFields: ControlField list option) (systemForm:Entity) =
    let bpfFields = bpfFields |? []
    let form = XElement.Parse(systemForm.Attributes.["formxml"].ToString())

    let tabs = 
      form.Descendants(XName.Get("tab"))
      |> Seq.choose (fun c -> 
        let tabName = getValue c "name"
        if tabName = null then None
        else

        let sections = 
          c.Descendants(XName.Get("section"))
          |> Seq.map (fun s -> getValue s "name")
          |> Seq.filter (fun s -> s <> null && s.Length > 0)
          |> List.ofSeq

        Some (Utility.sanitizeString tabName, tabName, sections))
      |> Seq.filter (fun (iname, _, _) -> iname <> null && iname.Length > 0) 
      |> List.ofSeq


    // Attributes and controls
    let controlFields = 
      form.Descendants(XName.Get("control"))
      |> Seq.map (fun c -> 
        let id = getValue c "id"
        let classId = getValue c "classid"
        let controlClass = getControlClass id classId
          
        let datafieldname = getValue c "datafieldname"

        id, datafieldname, controlClass)
      |> List.ofSeq

    let name = systemForm.Attributes.["name"].ToString()
    let typeInt = (systemForm.Attributes.["type"] :?> OptionSetValue).Value
    
    { XrmForm.name =  name |> Utility.sanitizeString
      entityName =  systemForm.Attributes.["objecttypecode"].ToString()
      formType = enum<FormType>(typeInt).ToString()
      attributes = 
        controlFields @ bpfFields
        |> List.choose (getAttribute enums)
        |> List.distinctBy (fun x -> fst x)

      controls = 
        controlFields @ bpfFields
        |> List.map getControl
        |> renameControls
      tabs = tabs
    }

  /// Main function to interpret a grouping of FormXmls
  let interpretFormXmls (entityMetadata: XrmEntity[]) (formData:Map<string,Entity[]>) (bpfControls:Map<string, ControlField list>) =
    entityMetadata
    |> Array.map (fun em ->
        let enums = 
          em.attr_vars
          |> List.filter (fun attr -> attr.specialType = SpecialType.OptionSet)
          |> List.map (fun attr -> attr.logicalName, attr.varType)
          |> Map.ofList
        
        formData.[em.logicalName]
        |> Array.Parallel.map (interpretFormXml enums (bpfControls.TryFind em.logicalName)))
      |> Array.concat