﻿module private XRoad.ProducerDefinition

open System
open System.CodeDom
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Xml
open System.Xml.Serialization

open XRoad.CodeDom.Common
open XRoad.CodeDom.ServiceImpl
open XRoad.Common
open XRoad.ServiceDescription
open XRoad.TypeSchema

/// Functions and types to handle type building process.
module TypeBuilder =
    /// Describes single property for type declaration.
    type private PropertyDefinition =
        { /// Name of the property.
          Name: string
          /// Runtime type to use on property.
          Type: RuntimeType
          /// Does property accept nil values?
          IsNillable: bool
          /// Can array items be nil values?
          IsItemNillable: bool option
          /// Extra types to add as nested type declarations to owner type.
          AddedTypes: CodeTypeDeclaration list
          /// Can property value be unspecified in resulting SOAP message.
          IsOptional: bool
          /// Does array type property specify wrapper element around items?
          IsWrappedArray: bool option
          // Attribute type:
          IsAttribute: bool
          IsAny: bool
          IsIgnored: bool
          // Choice element specific attributes:
          ChoiceIdentifier: string option
          ChoiceElements: PropertyDefinition list }
        /// Initializes default property with name and optional value.
        static member Create(name, isOptional) =
            { Type = RuntimeType.PrimitiveType(typeof<System.Void>)
              IsNillable = false
              IsItemNillable = None
              AddedTypes = []
              IsOptional = isOptional
              IsWrappedArray = None
              Name = name
              IsAttribute = false
              IsAny = false
              IsIgnored = false
              ChoiceIdentifier = None
              ChoiceElements = [] }

    /// Build property declarations from property definitions and add them to owner type.
    let private addTypeProperties definitions ownerTy =
        definitions
        |> List.iter (fun definition ->
            // Most of the conditions handle XmlSerializer specific attributes.
            let prop = ownerTy |> addProperty(definition.Name, definition.Type, definition.IsOptional)
            if definition.IsIgnored then
                prop |> Prop.describe Attributes.XmlIgnore |> ignore
            elif definition.IsAny then
                prop |> Prop.describe Attributes.XmlAnyElement |> ignore
            elif definition.IsAttribute then
                prop |> Prop.describe Attributes.XmlAttribute |> ignore
            else
                match definition.IsWrappedArray, definition.Type with
                | Some(true), CollectionType(_, itemName, _) ->
                    prop |> Prop.describe (Attributes.XmlArray(definition.IsNillable))
                         |> Prop.describe (Attributes.XmlArrayItem(itemName, definition.IsItemNillable.Value))
                         |> ignore
                | Some(true), _ ->
                    failwith "Wrapped array should match to CollectionType."
                | (None | Some(false)), _ ->
                    if definition.ChoiceIdentifier.IsNone then
                        prop |> Prop.describe (Attributes.XmlElement(definition.IsNillable))
                             |> ignore
                match definition.ChoiceIdentifier with
                | Some(identifierName) ->
                    prop |> Prop.describe (Attributes.XmlChoiceIdentifier(identifierName)) |> ignore
                    definition.ChoiceElements
                    |> List.iter (fun x -> prop |> Prop.describe (Attributes.XmlElement2(x.Name, x.Type.AsCodeTypeReference()))
                                                |> ignore)
                | None -> ()
            // Add extra types to owner type declaration.
            definition.AddedTypes |> List.iter (fun x -> ownerTy |> Cls.addMember x |> ignore))

    /// Create definition of property that accepts any element not defined in schema.
    let private buildAnyProperty () =
        let prop = PropertyDefinition.Create("AnyElements", false)
        { prop with Type = PrimitiveType(typeof<XmlElement[]>); IsAny = true }

    /// Populate generated type declaration with properties specified in type schema definition.
    let rec build (context: TypeBuilderContext) runtimeType schemaType =
        // Extract type declaration from runtime type definition.
        let providedTy, providedTypeName =
            match runtimeType with
            | ProvidedType(decl, name) -> decl, name
            | _ -> failwith "Only generated types are accepted as arguments!"
        // Generates unique type name for every choice element.
        let choiceNameGenerator =
            let num = ref 0
            (fun () ->
                num := !num + 1
                sprintf "Choice%d" !num)
        // Parse schema definition and add all properties that are defined.
        match schemaType with
        | SimpleType(SimpleTypeSpec.Restriction(spec)) ->
            match context.GetRuntimeType(SchemaType(spec.Base)) with
            | PrimitiveType(_) as rtyp ->
                providedTy |> addProperty("BaseValue", rtyp, false) |> Prop.describe Attributes.XmlText |> ignore
            | ContentType ->
                providedTy |> inheritBinaryContent |> ignore
            | _ ->
                failwith "Simple types should not restrict complex types."
        | SimpleType(ListDef) ->
            failwith "Not implemented: list in simpleType."
        | SimpleType(Union(_)) ->
            failwith "Not implemented: union in simpleType."
        | ComplexType(spec) ->
            // Abstract types will have only protected constructor.
            if spec.IsAbstract then
                providedTy |> Cls.addAttr TypeAttributes.Abstract
                           |> Cls.addMember (Ctor.create() |> Ctor.setAttr MemberAttributes.Family)
                           |> ignore
            // Handle complex type content and add properties for attributes and elements.
            let specContent =
                match spec.Content with
                | SimpleContent(SimpleContentSpec.Extension(spec)) ->
                    match context.GetRuntimeType(SchemaType(spec.Base)) with
                    | PrimitiveType(_)
                    | ContentType as rtyp ->
                        providedTy |> addProperty("BaseValue", rtyp, false) |> Prop.describe Attributes.XmlText |> ignore
                        spec.Content
                    | _ ->
                        failwith "ComplexType-s simpleContent should not extend complex types."
                | SimpleContent(SimpleContentSpec.Restriction(_)) ->
                    failwith "Not implemented: restriction in complexType-s simpleContent."
                | ComplexContent(ComplexContentSpec.Extension(spec)) ->
                    match context.GetRuntimeType(SchemaType(spec.Base)) with
                    | ProvidedType(baseDecl,_) as baseTy ->
                        providedTy |> Cls.setParent (baseTy.AsCodeTypeReference()) |> ignore
                        baseDecl |> Cls.describe (Attributes.XmlInclude(typeRefName providedTypeName)) |> ignore
                    | _ ->
                        failwithf "Only complex types can be inherited! (%A)" spec.Base
                    spec.Content
                | ComplexContent(ComplexContentSpec.Restriction(_)) ->
                    failwith "Not implemented: restriction in complexType-s complexContent"
                | ComplexTypeContent.Particle(spec) ->
                    spec
            providedTy |> addTypeProperties (collectComplexTypeContentProperties choiceNameGenerator context specContent)
        | EmptyType -> ()

    /// Collects property definitions from every content element of complexType.
    and private collectComplexTypeContentProperties choiceNameGenerator context spec =
        // Attribute definitions
        let attributeProperties = spec.Attributes |> List.map (buildAttributeProperty context)
        // Element definitions
        let elementProperties =
            match spec.Content with
            | Some(ComplexTypeParticle.All(spec)) ->
                if spec.MinOccurs <> 1u || spec.MaxOccurs <> 1u then failwith "not implemented"
                spec.Elements |> List.map (buildElementProperty context)
            | Some(ComplexTypeParticle.Sequence(spec)) ->
                if spec.MinOccurs <> 1u || spec.MaxOccurs <> 1u then failwith "not implemented"
                spec.Content
                |> List.map (fun item ->
                    match item with
                    | SequenceContent.Choice(cspec) ->
                        collectChoiceProperties choiceNameGenerator context cspec
                    | SequenceContent.Element(spec) ->
                        [ buildElementProperty context spec ]
                    | SequenceContent.Sequence(_) ->
                        failwith "Not implemented: sequence in complexType sequence."
                    | SequenceContent.Any ->
                        [ buildAnyProperty() ]
                    | SequenceContent.Group ->
                        failwith "Not implemented: group in complexType sequence.")
                |> List.collect (id)
            | Some(ComplexTypeParticle.Choice(cspec)) ->
                collectChoiceProperties choiceNameGenerator context cspec
            | Some(ComplexTypeParticle.Group) ->
                failwith "Not implemented: group in complexType."
            | None -> []
        List.concat [attributeProperties; elementProperties]

    /// Create single property definition for given element-s schema specification.
    and private buildElementProperty (context: TypeBuilderContext) (spec: ElementSpec) =
        let name, schemaType = context.GetElementDefinition(spec)
        buildPropertyDef schemaType spec.MaxOccurs name spec.IsNillable (spec.MinOccurs = 0u) context

    /// Create single property definition for given attribute-s schema specification.
    and private buildAttributeProperty (context: TypeBuilderContext) (spec: AttributeSpec) =
        let name, schemaObject = context.GetAttributeDefinition(spec)
        // Resolve schema type for attribute:
        let schemaType =
            match schemaObject with
            | Definition(simpleTypeSpec) -> Definition(SimpleType(simpleTypeSpec))
            | Name(name) -> Name(name)
            | Reference(ref) -> Reference(ref)
        let isOptional = match spec.Use with Required -> true | _ -> false
        let prop = buildPropertyDef schemaType 1u name false isOptional context
        { prop with IsAttribute = true }

    /// Build default property definition from provided schema information.
    and private buildPropertyDef schemaType maxOccurs name isNillable isOptional context =
        let propertyDef = PropertyDefinition.Create(name, isOptional)
        match schemaType with
        | Definition(ArrayContent itemSpec) ->
            match context.GetElementDefinition(itemSpec) with
            | itemName, Name(n) ->
                { propertyDef with
                    Type = CollectionType(context.GetRuntimeType(SchemaType(n)), itemName, None)
                    IsNillable = isNillable
                    IsItemNillable = Some(itemSpec.IsNillable)
                    IsWrappedArray = Some(true) }
            | itemName, Definition(def) ->
                let suffix = itemName.toClassName()
                let typ = Cls.create(name + suffix) |> Cls.addAttr TypeAttributes.Public
                let runtimeType = ProvidedType(typ, typ.Name)
                build context runtimeType def
                { propertyDef with
                    Type = CollectionType(runtimeType, itemName, None)
                    IsNillable = isNillable
                    IsItemNillable = Some(itemSpec.IsNillable)
                    AddedTypes = [typ]
                    IsWrappedArray = Some(true) }
            | _, Reference(_) -> failwith "never"
        | Definition(def) ->
            let subTy = Cls.create (name + "Type") |> Cls.addAttr TypeAttributes.Public
            let runtimeType = ProvidedType(subTy, subTy.Name)
            build context runtimeType def
            if maxOccurs > 1u then
                { propertyDef with
                    Type = CollectionType(runtimeType, name, None)
                    IsNillable = isNillable
                    AddedTypes = [subTy]
                    IsWrappedArray = Some(false) }
            else
                { propertyDef with
                    Type = runtimeType
                    IsNillable = isNillable
                    AddedTypes = [subTy] }
        | Name(n) ->
            match context.GetRuntimeType(SchemaType(n)) with
            | x when maxOccurs > 1u ->
                { propertyDef with
                    Type = CollectionType(x, name, None)
                    IsNillable = isNillable
                    IsWrappedArray = Some(false) }
            | PrimitiveType(x) when x.IsValueType ->
                { propertyDef with
                    Type = PrimitiveType(if isNillable then typedefof<Nullable<_>>.MakeGenericType(x) else x)
                    IsNillable = isNillable }
            | x ->
                { propertyDef with
                    Type = x
                    IsNillable = isNillable }
        | Reference(_) ->
            failwith "Not implemented: schema reference to type."

    /// Create property definitions for choice element specification.
    and private collectChoiceProperties choiceNameGenerator context spec : PropertyDefinition list =
        match buildChoiceMembers spec context with
        | [] -> []
        | [ _ ] -> failwith "Not implemented: single option choice should be treated as regular sequence."
        | options ->
            // New unique name for choice properties and types.
            let choiceName = choiceNameGenerator()
            // Create enumeration type for options.
            let choiceEnum =
                Cls.createEnum (choiceName + "Type")
                |> Cls.setAttr TypeAttributes.Public
                |> Cls.describe Attributes.XmlTypeExclude
            let isArray = options |> List.map (List.length) |> List.max > 1
            let enumNameType =
                let rt = ProvidedType(choiceEnum, choiceEnum.Name)
                if isArray then CollectionType(rt, "", None) else rt
            let choiceTypeProp =
                let prop = PropertyDefinition.Create(choiceName + "Name", false)
                { prop with Type = enumNameType; IsIgnored = true; AddedTypes = [choiceEnum] }
            // Create property for holding option values.
            let choiceItemType =
                let rt =
                    options
                    |> List.collect (id)
                    |> List.fold (fun (s: RuntimeType option) x ->
                        match s with
                        | None -> Some(x.Type)
                        | Some(y) when x.Type = y -> s
                        | _ -> Some(PrimitiveType(typeof<obj>))) None
                    |> Option.get
                if isArray then CollectionType(rt, "", None) else rt
            let choiceElements = options |> List.collect (id)
            choiceElements
            |> List.iter (fun opt ->
                let fld =
                    Fld.createEnum (choiceName + "Type") opt.Name
                    |> Fld.describe (Attributes.XmlEnum opt.Name)
                choiceEnum
                |> Cls.addMember fld
                |> ignore)
            let choiceItemProp =
                let prop = PropertyDefinition.Create(choiceName + (if isArray then "Items" else"Item"), false)
                { prop with Type = choiceItemType; ChoiceIdentifier = Some(choiceTypeProp.Name); ChoiceElements = choiceElements }
            [ choiceTypeProp; choiceItemProp ]

    /// Extract property definitions for all the elements defined in sequence element.
    and private buildSequenceMembers context (spec: SequenceSpec) =
        spec.Content
        |> List.map (
            function
            | SequenceContent.Any ->
                failwith "Not implemented: any in sequence."
            | SequenceContent.Choice(_) ->
                failwith "Not implemented: choice in sequence."
            | SequenceContent.Element(espec) ->
                buildElementProperty context espec
            | SequenceContent.Group ->
                failwith "Not implemented: group in sequence."
            | SequenceContent.Sequence(_) ->
                failwith "Not implemented: sequence in sequence.")

    /// Extract property definitions for all the elements defined in choice element.
    and private buildChoiceMembers (spec: ChoiceSpec) context =
        spec.Content
        |> List.map (
            function
            | ChoiceContent.Any ->
                failwith "Not implemented: any in choice."
            | ChoiceContent.Choice(_) ->
                failwith "Not implemented: choice in choice."
            | ChoiceContent.Element(espec) ->
                [ buildElementProperty context espec ]
            | ChoiceContent.Group ->
                failwith "Not implemented: group in choice."
            | ChoiceContent.Sequence(sspec) ->
                buildSequenceMembers context sspec)

/// Functions and types to handle building methods for services and operation bindings.
module ServiceBuilder =
    /// Creates return type for the operation.
    /// To support returning multiple output parameters, they are wrapped into tuples accordingly:
    /// Single parameter responses return that single parameter.
    /// Multiple parameter responses are wrapped into tuples, since C# provides tuples upto 8 arguments,
    /// some composition is required when more output parameters are present.
    let private makeReturnType isMultipart (types: (string * RuntimeType) list) =
        let rec getReturnTypeTuple (tuple: (string * RuntimeType) list, types) =
            match types with
            | [] -> let typ = CodeTypeReference("System.Tuple", tuple |> List.map (fun (_,typ) -> typ.AsCodeTypeReference()) |> Array.ofList)
                    (typ, Expr.instOf typ (tuple |> List.map (fun (varName,_) -> Expr.var varName)))
            | x::xs when tuple.Length < 7 -> getReturnTypeTuple(x :: tuple, xs)
            | x::xs -> let inner = getReturnTypeTuple([x], xs)
                       let typ = CodeTypeReference("System.Tuple", ((tuple |> List.map (fun (_,typ) -> typ.AsCodeTypeReference())) @ [fst inner]) |> Array.ofList)
                       (typ, Expr.instOf typ ((tuple |> List.map (fun (varName,_) -> Expr.var varName)) @ [snd inner]))
        let types =
            if isMultipart
            then ("responseAttachments", PrimitiveType(typeof<IDictionary<string,Stream>>))::types
            else types
        match types with
        | [] -> (CodeTypeReference(typeof<Void>), Expr.empty)
        | (varName, typ)::[] -> (typ.AsCodeTypeReference(), Expr.var varName)
        | many -> getReturnTypeTuple([], many)

    /// Initializes overrides for multipart serialization mode.
    let private initMultipart prefix isMultipart = seq {
        if isMultipart then
            yield Stmt.declVarWith<XmlAttributeOverrides> (prefix + "Overrides") (Expr.inst<XmlAttributeOverrides> [])
            yield Stmt.declVarWith<XmlAttributes> (prefix + "Attributes") (Expr.inst<XmlAttributes> [])
            yield Stmt.assign (Expr.var (prefix + "Attributes") @=> "XmlIgnore") (Expr.value true)
            yield Stmt.ofExpr ((Expr.var (prefix + "Overrides") @-> "Add") @% [Expr.typeOf (typeRefName "BinaryContent"); Expr.value "Value"; Expr.var (prefix + "Attributes")])
        else
            yield Stmt.declVarWith<XmlAttributeOverrides> (prefix + "Overrides") Expr.nil
        }

    /// Generate types and serializing statements for root entities.
    /// Returns root name and type info.
    let private findRootNameAndType (context: TypeBuilderContext) part =
        match context.Style, part.SchemaEntity with
        | DocLiteral, SchemaType(t) ->
            failwithf "Document/Literal style message part '%s' should reference global element as message part, but type '%s' is used instead" part.Name t.LocalName
        | RpcEncoded, SchemaElement(e) ->
            failwithf "RPC/Encoded style message part '%s' should reference global type as message part, but element '%s' is used instead" part.Name e.LocalName
        | DocLiteral, SchemaElement(elementName) ->
            match context.GetElementDefinition(context.GetElementSpec(elementName)) with
            | _, Definition(_) ->
                // Inline element type definition uses same name as global element.
                (None, None), (SchemaElement(elementName))
            | _, Name(name) ->
                // Element references globally defined type, so parameter should use same runtime type.
                (Some(elementName.LocalName), Some(elementName.NamespaceName)), (SchemaType(name))
            | _ ->
                // Ref attribute will be resolved earlier.
                failwith "never"
        | RpcEncoded, SchemaType(_) ->
            // Use the same type that is referenced from message part definition.
            (Some(part.Name), None), (part.SchemaEntity)

    /// Initialize root attribute override for this method parameter.
    let initRoot varName name ns = seq {
        match name with
        | Some(v) ->
            yield Stmt.declVarWith<XmlRootAttribute> varName (Expr.inst<XmlRootAttribute> [Expr.value v])
            match ns with
            | Some(v) -> yield Stmt.assign (Expr.var varName @=> "Namespace") (Expr.value v)
            | None -> ()
        | None ->
            yield Stmt.declVarWith<XmlRootAttribute> varName (Expr.inst<XmlRootAttribute> [Expr.nil])
        }

    /// Create expression which writes start of specified root element.
    let writeRootElementExpr rootName rootNamespace =
        let rootElementName =
            [rootName; rootName |> Option.bind (fun _ -> rootNamespace)]
            |> List.choose (id)
            |> List.map (Expr.value)
        (Expr.var "writer" @-> "WriteStartElement") @% rootElementName

    /// Create serializer code segment for given parameter.
    let private serializeParameter (context: TypeBuilderContext) isMultipart serviceMethod (part: MessagePart) =
        let serializerName = part.Name + "Serializer"
        let overridesName = part.Name + "Overrides"
        let rootName = part.Name + "Root"
        // Initialize serializing
        let initSerializing schemaName (rnm, rns : string option) =
            // Add new argument to method for this message part.
            let runtimeType = context.GetRuntimeType(schemaName)
            serviceMethod |> Meth.addParamRef (runtimeType.AsCodeTypeReference()) part.Name |> ignore
            // Add overrides to support multipart attachment serialization.
            initMultipart part.Name isMultipart
            |> Seq.iter (fun s -> serviceMethod |> Meth.addStmt s |> ignore)
            // Collection types need special handling for root element.
            match runtimeType with
            | CollectionType(itemType, itemName, _) ->
                // Write array wrapper manually and serialize each array item separately.
                serviceMethod
                |> Meth.addExpr (writeRootElementExpr rnm rns)
                |> Meth.addStmt (Stmt.condIfElse (Op.isNull (Expr.var part.Name))
                                                 [Stmt.ofExpr ((Expr.var "writer" @-> "WriteAttributeString") @% [Expr.value "nil"; Expr.value XmlNamespace.Xsi; Expr.value "true"])]
                                                 [Stmt.declVarWith<XmlRootAttribute> rootName (Expr.inst<XmlRootAttribute> [Expr.value itemName])
                                                  Stmt.forLoop (Stmt.declVarWith<int> "i" (Expr.value 0))
                                                               (Op.greater (Expr.var part.Name @=> "Length") (Expr.var "i"))
                                                               (Stmt.assign (Expr.var "i") (Op.plus (Expr.var "i") (Expr.value 1)))
                                                               [Stmt.declVarWith<XmlSerializer> serializerName (Expr.inst<XmlSerializer> [Expr.typeOf (itemType.AsCodeTypeReference()); Expr.var overridesName; Arr.createOfSize<Type> 0; Expr.var rootName; Expr.nil ])
                                                                Stmt.ofExpr ((Expr.var serializerName @-> "Serialize") @% [Expr.var "writer"; Expr.var part.Name @? (Expr.var "i")])]])
                |> Meth.addExpr ((Expr.var "writer" @-> "WriteEndElement") @% [])
                |> ignore
            | _ ->
                initRoot rootName rnm rns
                |> Seq.iter (fun s -> serviceMethod |> Meth.addStmt s |> ignore)
                serviceMethod
                |> Meth.addStmt (Stmt.declVarWith<XmlSerializer> serializerName (Expr.inst<XmlSerializer> [Expr.typeOf (runtimeType.AsCodeTypeReference()); Expr.var overridesName; Arr.createOfSize<Type> 0; Expr.var rootName; Expr.nil ]))
                |> Meth.addExpr ((Expr.var serializerName @-> "Serialize") @% [Expr.var "writer"; Expr.var part.Name])
                |> ignore
        let rootName, typeName = findRootNameAndType context part
        initSerializing typeName rootName

    /// Create deserializer code segment for given parameter.
    let private buildDeserialization (context: TypeBuilderContext) undescribedFaults (message: OperationMessage) : CodeTypeReference * CodeStatement list =
        let statements = List<CodeStatement>()
        let returnType = List<string * RuntimeType>()

        // Create separate deserializer calls for each output parameter.
        message.Body.Parts
        |> List.iteri (fun i part ->
            let (rootName, rootNamespace), typeName = findRootNameAndType context part
            let serializerName = part.Name + "Serializer"
            let overridesName = part.Name + "Overrides"
            let rootVarName = part.Name + "Root"
            let runtimeType = context.GetRuntimeType(typeName)
            returnType.Add(part.Name, runtimeType)
            statements.AddRange(initMultipart part.Name message.IsMultipart)
            let deserializerCallExpr =
                match runtimeType with
                | CollectionType(itemType, itemName, _) ->
                    let listType = CodeTypeReference("System.Collections.Generic.List", itemType.AsCodeTypeReference())
                    let nullVarName = sprintf "v%dNull" i
                    let listVarName = sprintf "v%dList" i
                    let typ = itemType.AsCodeTypeReference()
                    [ Stmt.declVarWith<string> nullVarName ((Expr.var "reader" @-> "GetAttribute") @% [Expr.value "nil"; Expr.value XmlNamespace.Xsi])
                      Stmt.condIfElse (Op.boolAnd (Op.isNotNull (Expr.var nullVarName))
                                                  (Op.equals ((((Expr.var nullVarName @-> "ToLower") @% []) @-> "Replace") @% [Expr.value "true"; Expr.value "1"])
                                                             (Expr.value "1")))
                                      [ Stmt.assign (Expr.var (sprintf "v%d" i)) Expr.nil ]
                                      [ Stmt.declVarRefWith listType listVarName (Expr.instOf listType [])
                                        Stmt.declVarWith<XmlRootAttribute> rootVarName (Expr.inst<XmlRootAttribute> [Expr.value itemName])
                                        Stmt.declVarWith<XmlSerializer> serializerName (Expr.inst<XmlSerializer> [Expr.typeOf typ; Expr.var overridesName; Arr.createOfSize<Type> 0; Expr.var rootVarName; Expr.nil])
                                        Stmt.whileLoop (Expr.var "MoveToElement" @%% [Expr.var "reader"; Expr.nil; Expr.nil; Expr.value 4])
                                                       [Stmt.ofExpr ((Expr.var listVarName @-> "Add") @% [Expr.cast typ ((Expr.var serializerName @-> "Deserialize") @% [Expr.var "reader"])])]
                                        Stmt.assign (Expr.var (sprintf "v%d" i)) ((Expr.var listVarName @-> "ToArray") @% []) ] ]
                | _ ->
                    let typ = runtimeType.AsCodeTypeReference()
                    statements.AddRange(initRoot rootVarName rootName rootNamespace)
                    [ Stmt.declVarWith<XmlSerializer> serializerName (Expr.inst<XmlSerializer> [Expr.typeOf typ; Expr.var overridesName; Arr.createOfSize<Type> 0; Expr.var rootVarName; Expr.nil])
                      Stmt.assign (Expr.var (sprintf "v%d" i)) (Expr.cast typ ((Expr.var serializerName @-> "Deserialize") @% [Expr.var "reader"])) ]
            let deserializeExpr =
                if part.Name = "keha" && undescribedFaults then
                    [ Stmt.ofExpr((Expr.var "reader" @-> "SetBookmark") @% [Expr.value "keha"])
                      Stmt.condIfElse (Expr.var "MoveToElement" @%% [Expr.var "reader"; Expr.value "faultCode"; Expr.value ""; Expr.value 4])
                                      [ Stmt.ofExpr ((Expr.var "reader" @-> "ReturnToAndRemoveBookmark") @% [Expr.value "keha"])
                                        Stmt.throw<Exception> [(Expr.var "reader" @-> "ReadInnerXml") @% []] ]
                                      (Stmt.ofExpr ((Expr.var "reader" @-> "ReturnToAndRemoveBookmark") @% [Expr.value "keha"]) :: deserializerCallExpr) ]
                else deserializerCallExpr
            statements.Add(Stmt.condIf (Op.equals (Expr.var "reader" @=> "LocalName")
                                                    (Expr.value part.Name))
                                        deserializeExpr))

        // Build return type and expression which returns the value.
        let retType, returnExpr = makeReturnType message.IsMultipart (returnType |> Seq.mapi (fun i (_,rt) -> (sprintf "v%d" i), rt) |> List.ofSeq)

        // Build body part of deserialization block.
        let bodyStatements = List<_>()
        bodyStatements.AddRange(returnType |> Seq.mapi (fun i (_,runtimeType) -> Stmt.declVarRefWith (runtimeType.AsCodeTypeReference()) (sprintf "v%d" i) Expr.nil))
        bodyStatements.Add(Stmt.whileLoop (Expr.var "MoveToElement" @%% [Expr.var "reader"; Expr.nil; Expr.nil; Expr.value 3]) (statements |> List.ofSeq))
        bodyStatements.Add(Stmt.ret returnExpr)

        retType, bodyStatements |> List.ofSeq

    /// Build content for each individual service call method.
    let build context undescribedFaults (operation: Operation) =
        let serviceMethod =
            Meth.create operation.Name.LocalName
            |> Meth.setAttr (MemberAttributes.Public ||| MemberAttributes.Final)

        // Add documentation to the method if present.
        match operation.Documentation.TryGetValue("et") with
        | true, doc -> serviceMethod.Comments.Add(CodeCommentStatement(doc, true)) |> ignore
        | _ -> ()

        let requiredHeadersExpr = operation.GetRequiredHeaders() |> List.map Expr.value |> Arr.create<string>
        let serviceName = match operation.Version with Some v -> sprintf "%s.%s" operation.Name.LocalName v | _ -> operation.Name.LocalName

        // CodeDom doesn't support delegates, so we have to improvise.
        serviceMethod
        |> Meth.addStmt (Stmt.declVarWith<string[]> "requiredHeaders" requiredHeadersExpr)
        |> Meth.addStmt (Stmt.declVarWith<Action<XmlWriter>> "writeHeader" (Expr.code "(writer) => { //"))
        |> Meth.addExpr ((Expr.parent @-> "WriteHeader") @% [Expr.var "writer"; Expr.value serviceName; Expr.var "requiredHeaders"])
        |> Meth.addExpr (Expr.code "}")
        |> Meth.addStmt (Stmt.declVarWith<Action<XmlWriter>> "writeBody" (Expr.code "(writer) => { //"))
        |> Meth.addExpr ((Expr.var "writer" @-> "WriteAttributeString") @% [Expr.value "xmlns"; Expr.value "svc"; Expr.nil; Expr.value operation.Name.NamespaceName])
        |> iif (operation.Request.Body.Namespace <> operation.Name.NamespaceName) (fun x -> x |> Meth.addExpr ((Expr.var "writer" @-> "WriteAttributeString") @% [Expr.value "xmlns"; Expr.value "svcns"; Expr.nil; Expr.value operation.Request.Body.Namespace]))
        |> iif (operation.Style = RpcEncoded) (fun x -> x |> Meth.addExpr ((Expr.var "writer" @-> "WriteStartElement") @% [Expr.value operation.Request.Name.LocalName; Expr.value operation.Request.Body.Namespace]))
        |> ignore

        // Create separate serializer for each parameter.
        operation.Request.Body.Parts
        |> List.iter (serializeParameter context operation.Request.IsMultipart serviceMethod)

        // Finish body writer delegate.
        serviceMethod
        |> iif (operation.Style = RpcEncoded) (fun x -> x |> Meth.addExpr ((Expr.var "writer" @-> "WriteEndElement") @% []))
        |> Meth.addExpr (Expr.code "}")
        |> ignore

        // Build output parameters deserialization expressions.
        let returnType, deserializeExpr = buildDeserialization context undescribedFaults operation.Response

        // Reading body of response.
        serviceMethod
        |> Meth.returnsOf returnType
        |> Meth.addStmt (Stmt.declVarRefWith (CodeTypeReference("System.Func", typeRef<XmlReader>, typeRef<IDictionary<string,Stream>>, returnType)) "readBody" (Expr.code "(r, responseAttachments) => { //"))
        |> Meth.addStmt (if undescribedFaults
                         then Stmt.declVarRefWith (typeRefName "XmlBookmarkReader") "reader" (Expr.cast (typeRefName "XmlBookmarkReader") (Expr.var "r"))
                         else Stmt.declVarWith<XmlReader> "reader" (Expr.var "r"))
        |> Meth.addStmt (Stmt.condIf (Op.boolOr (Op.notEquals (Expr.var "reader" @=> "LocalName")
                                                              (Expr.value operation.Response.Name.LocalName))
                                                (Op.notEquals (Expr.var "reader" @=> "NamespaceURI")
                                                              (Expr.value operation.Response.Body.Namespace)))
                                     [Stmt.throw<Exception> [Expr.value "Invalid response message."]])
        |> ignore

        // Deserialize main content.
        deserializeExpr |> List.iter (fun s -> serviceMethod |> Meth.addStmt s |> ignore)

        // Finish delegate block.
        serviceMethod |> Meth.addExpr (Expr.code "}") |> ignore

        // Multipart services have separate argument: list of MIME/multipart attachments.
        let attachmentsExpr =
            if operation.Request.IsMultipart then
                serviceMethod |> Meth.addParam<IDictionary<string,Stream>> "attachments" |> ignore
                Expr.var "attachments"
            else Expr.nil

        // Execute base class service call method with custom serialization delegates.
        let methodCall = ((Expr.parent @-> "MakeServiceCall") @<> [returnType]) @% [attachmentsExpr; Expr.var "writeHeader"; Expr.var "writeBody"; Expr.var "readBody"]

        // Make return statement if definition specifies result.
        let isEmptyResponse = (not operation.Response.IsMultipart) && (operation.Response.Body.Parts |> List.isEmpty)
        serviceMethod |> Meth.addStmt (if isEmptyResponse then Stmt.ofExpr methodCall else Stmt.ret methodCall)

/// Builds all types, namespaces and services for give producer definition.
/// Called by type provider to retrieve assembly details for generated types.
let makeProducerType (typeNamePath: string [], producerUri, undescribedFaults) =
    // Load schema details from specified file or network location.
    let schema = ProducerDescription.Load(resolveUri producerUri)

    // Initialize type and schema element lookup context.
    let context = TypeBuilderContext.FromSchema(schema)

    // Create base type which provides access to service calls.
    let portBaseTy = makeServicePortBaseType undescribedFaults context.Style

    // Create base type which holds types generated from all provided schema-s.
    let serviceTypesTy = Cls.create "DefinedTypes" |> Cls.setAttr TypeAttributes.Public |> Cls.asStatic

    // Build all global types for each type schema definition.
    schema.TypeSchemas
    |> Map.toSeq
    |> Seq.collect (fun (_, typeSchema) -> typeSchema.Types)
    |> Seq.choose (fun x ->
        match context.GetRuntimeType(SchemaType(x.Key)) with
        | CollectionType(prtyp, _, Some(st)) -> Some(prtyp, st)
        | CollectionType(_, _, None) -> None
        | rtyp -> Some(rtyp, x.Value))
    |> Seq.iter (fun (rtyp, def) -> TypeBuilder.build context rtyp def)

    // Build all global elements for each type schema definition.
    schema.TypeSchemas
    |> Map.toSeq
    |> Seq.collect (fun (_, typeSchema) -> typeSchema.Elements)
    |> Seq.choose (fun x ->
        match x.Value.Type with
        | Definition(_) -> Some(context.GetRuntimeType(SchemaElement(x.Key)), x.Value)
        | _ -> None)
    |> Seq.iter (fun (typ, spec) ->
        match spec.Type with
        | Definition(def) -> TypeBuilder.build context typ def
        | Reference(_) -> failwith "Root level element references are not allowed."
        | Name(_) -> ())

    // Main class that wraps all provided functionality and types.
    let targetClass =
        Cls.create typeNamePath.[typeNamePath.Length - 1]
        |> Cls.setAttr TypeAttributes.Public
        |> Cls.asStatic
        // Undescribed faults require looser navigation in XmlReader.
        |> iif undescribedFaults (fun x -> x |> Cls.addMember (createTypeFromAssemblyResource("XmlBookmarkReader.cs")))
        |> Cls.addMember portBaseTy
        |> Cls.addMember serviceTypesTy
        |> Cls.addMember (createBinaryContentType())

    // Create methods for all operation bindings.
    schema.Services
    |> List.iter (fun service ->
        let serviceTy = Cls.create service.Name |> Cls.setAttr TypeAttributes.Public |> Cls.asStatic
        service.Ports
        |> List.iter (fun port ->
            let ctor =
                Ctor.create()
                |> Ctor.setAttr MemberAttributes.Public
                |> Ctor.addBaseArg (Expr.value port.Address)
                |> Ctor.addBaseArg (Expr.value port.Producer)
            let portTy =
                Cls.create port.Name
                |> Cls.setAttr TypeAttributes.Public
                |> Cls.setParent (typeRefName portBaseTy.Name)
                |> Cls.addMember ctor
            serviceTy |> Cls.addMember portTy |> ignore
            match port.Documentation.TryGetValue("et") with
            | true, doc -> portTy.Comments.Add(CodeCommentStatement(doc, true)) |> ignore
            | _ -> ()
            port.Operations
            |> List.iter (fun op -> portTy |> Cls.addMember (ServiceBuilder.build context undescribedFaults op) |> ignore)
            )
        targetClass |> Cls.addMember serviceTy |> ignore
        )

    // Create types for all type namespaces.
    context.CachedNamespaces |> Seq.iter (fun kvp -> kvp.Value |> serviceTypesTy.Members.Add |> ignore)

    // Initialize default namespace to hold main type.
    let codeNamespace = CodeNamespace(String.Join(".", Array.sub typeNamePath 0 (typeNamePath.Length - 1)))
    codeNamespace.Types.Add(targetClass) |> ignore

    // Compile the assembly and return to type provider.
    let assembly = Compiler.buildAssembly(codeNamespace)
    assembly.GetType(sprintf "%s.%s" codeNamespace.Name targetClass.Name)