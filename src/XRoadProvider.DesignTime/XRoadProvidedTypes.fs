﻿namespace ProviderImplementation.ProvidedTypes

open Microsoft.FSharp.Core.CompilerServices
open System
open System.Collections.Concurrent
open System.Reflection
open XRoad

/// Generated type providers for X-Road infrastructure.
/// Currently only one type provider is available, which builds service interface for certain producer.
[<TypeProvider>]
type XRoadProducerProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces (config, assemblyReplacementMap = [("XRoadProvider.DesignTime", "XRoadProvider")])

    let ns = "XRoad.Providers"
    let asm = Assembly.GetExecutingAssembly()

    do assert (typeof<XRoad.BinaryContent>.Assembly.GetName().Name = asm.GetName().Name)

    // Already generated assemblies
    let typeCache = ConcurrentDictionary<_,ProvidedTypeDefinition>()

    // Available parameters to use for configuring type provider instance
    let staticParameters =
        [ ProvidedStaticParameter("Uri", typeof<string>), "WSDL document location (either local file or network resource)."
          ProvidedStaticParameter("LanguageCode", typeof<string>, "et"), "Specify language code that is extracted as documentation tooltips. Default value is estonian (et)."
          ProvidedStaticParameter("Filter", typeof<string>, ""), "Comma separated list of operations which should be included in definitions. By default, all operations are included." ]
        |> List.map (fun (parameter, doc) -> parameter.AddXmlDoc(doc); parameter)

    let createType typeName (uri, languageCode, filter: string) =
        let asm = ProvidedAssembly()

        // Same parameter set should have same output, so caching is reasonable.
        let key = (typeName, uri, languageCode, filter)
        match typeCache.TryGetValue(key) with
        | false, _ ->
            let operationFilter =
                match filter with
                | null -> []
                | value -> value.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun x -> x.Trim()) |> List.ofArray
            typeCache.GetOrAdd(key, (fun _ -> ProducerDefinition.makeProducerType (asm, ns, typeName) (uri, languageCode, operationFilter)))
        | true, typ -> typ

    let producerType =
        let t = ProvidedTypeDefinition(asm, ns, "XRoadProducer", Some typeof<obj>, isErased = false)
        t.AddXmlDoc("Type provider for generating service interfaces and data types for specific X-Road producer.")
        t.DefineStaticParameters(staticParameters, fun typeName args -> createType typeName (unbox<string> args.[0], unbox<string> args.[1], unbox<string> args.[2]))
        t

    do this.AddNamespace(ns, [producerType])

/// Erased type providers for X-Road infrastructure.
/// Currently only one type provider is available, which acquires list of all producers from
/// security server.
[<TypeProvider>]
type XRoadProviders(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config, assemblyReplacementMap = [("XRoadProvider.DesignTime", "XRoadProvider")])

    let theAssembly = typeof<XRoadProviders>.Assembly
    let namespaceName = "XRoad.Providers"
    let baseTy = typeof<obj>

    // Main type which provides access to producer list.
    let serverTy =
        let typ = ProvidedTypeDefinition(theAssembly, namespaceName, "XRoadServer", Some baseTy)
        typ.AddXmlDoc("Type provider which discovers available producers from specified X-Road security server.")
        typ

    do
        let serverIPParam = ProvidedStaticParameter("ServerIP", typeof<string>)
        serverIPParam.AddXmlDoc("IP address of X-Road security server which is used for producer discovery task.")

        serverTy.DefineStaticParameters(
            [ serverIPParam ],
            fun typeName parameterValues ->
                let thisTy = ProvidedTypeDefinition(theAssembly, namespaceName, typeName, Some baseTy)
                match parameterValues with
                | [| :? string as serverIP |] ->
                    // Create field which holds default service endpoint for the security server.
                    let requestUri = ProvidedField.Literal("RequestUri", typeof<string>, sprintf "http://%s/cgi-bin/consumer_proxy" serverIP)
                    thisTy.AddMember(requestUri)
                    // Create type which holds producer list.
                    let producersTy = ProvidedTypeDefinition("Producers", Some baseTy, HideObjectMethods=true)
                    producersTy.AddXmlDoc("List of available database names registered at the security server.")
                    thisTy.AddMember(producersTy)
                    // Add list of members which each corresponds to certain producer.
                    SecurityServer.discoverProducers(serverIP)
                    |> List.map (fun producer ->
                        let producerTy = ProvidedTypeDefinition(producer.Name, Some baseTy, HideObjectMethods=true)
                        producerTy.AddMember(ProvidedField.Literal("ProducerName", typeof<string>, producer.Name))
                        producerTy.AddMember(ProvidedField.Literal("WsdlUri", typeof<string>, producer.WsdlUri))
                        producerTy.AddXmlDoc(producer.Description)
                        producerTy)
                    |> producersTy.AddMembers
                | _ -> failwith "Unexpected parameter values!"
                thisTy)

    let noteProperty message : MemberInfo =
        let property = ProvidedProperty("<Note>", typeof<string>, getterCode = (fun _ -> <@@ "" @@>), isStatic = true)
        property.AddXmlDoc(message)
        upcast property

    let buildServer6Types (typeName: string) (args: obj []) =
        let securityServerUri = Uri(unbox<string> args.[0])
        let xRoadInstance: string = unbox args.[1]
        let memberClass: string = unbox args.[2]
        let memberCode: string = unbox args.[3]
        let subsystemCode: string = unbox args.[4]
        let refresh: bool = unbox args.[5]

        let client =
            match subsystemCode with
            | null | "" -> SecurityServerV6.Member(xRoadInstance, memberClass, memberCode)
            | code -> SecurityServerV6.Subsystem(xRoadInstance, memberClass, memberCode, code)

        let thisTy = ProvidedTypeDefinition(theAssembly, namespaceName, typeName, Some baseTy)

        // Type which holds information about producers defined in selected instance.
        let producersTy = ProvidedTypeDefinition("Producers", Some baseTy, HideObjectMethods = true)
        producersTy.AddXmlDoc("All available producers in particular v6 X-Road instance.")
        thisTy.AddMember(producersTy)

        // Type which holds information about central services defined in selected instance.
        let centralServicesTy = ProvidedTypeDefinition("CentralServices", Some baseTy, HideObjectMethods = true)
        centralServicesTy.AddXmlDoc("All available central services in particular v6 X-Road instance.")
        thisTy.AddMember(centralServicesTy)
        
        let identifier = ProvidedProperty("Identifier", typeof<XRoadMemberIdentifier>, isStatic = true, getterCode = (fun _ -> <@@ XRoadMemberIdentifier(xRoadInstance, memberClass, memberCode, subsystemCode) @@>))
        thisTy.AddMember(identifier)

        producersTy.AddMembersDelayed (fun _ ->
            try
                SecurityServerV6.downloadProducerList securityServerUri xRoadInstance refresh
                |> List.map (fun memberClass ->
                    let memberClassName = memberClass.Name
                    let classTy = ProvidedTypeDefinition(memberClass.Name, Some baseTy, HideObjectMethods = true)
                    classTy.AddXmlDoc(memberClass.Name)
                    classTy.AddMember(ProvidedField.Literal("ClassName", typeof<string>, memberClass.Name))
                    classTy.AddMembersDelayed (fun () ->
                        memberClass.Members
                        |> List.map (fun memberItem ->
                            let memberItemCode = memberItem.Code
                            let memberId = SecurityServerV6.Member(xRoadInstance, memberClassName, memberItemCode)
                            let addServices provider =
                                try
                                    let service: SecurityServerV6.Service = { Provider = provider; ServiceCode = "listMethods"; ServiceVersion = None }
                                    SecurityServerV6.downloadMethodsList securityServerUri client service
                                    |> List.map (fun x ->
                                        let versionSuffix = x.ServiceVersion |> Option.fold (fun _ x -> sprintf "/%s" x) ""
                                        ProvidedField.Literal((sprintf "SERVICE:%s%s" x.ServiceCode versionSuffix), typeof<string>, Uri(securityServerUri, x.WsdlPath).ToString()) :> MemberInfo)
                                with e -> [noteProperty (e.ToString())]
                            let memberTy = ProvidedTypeDefinition(sprintf "%s (%s)" memberItem.Name memberItem.Code, Some baseTy, HideObjectMethods = true)
                            memberTy.AddXmlDoc(memberItem.Name)
                            memberTy.AddMember(ProvidedField.Literal("Name", typeof<string>, memberItem.Name))
                            memberTy.AddMember(ProvidedField.Literal("Code", typeof<string>, memberItem.Code))
                            memberTy.AddMember(ProvidedProperty("Identifier", typeof<XRoadMemberIdentifier>, isStatic = true, getterCode = (fun _ -> <@@ XRoadMemberIdentifier(xRoadInstance, memberClassName, memberItemCode) @@>)))
                            memberTy.AddMembersDelayed(fun _ -> addServices memberId)
                            memberTy.AddMembersDelayed(fun () ->
                                memberItem.Subsystems
                                |> List.map (fun subsystem ->
                                    let subsystemId = memberId.GetSubsystem(subsystem)
                                    let subsystemTy = ProvidedTypeDefinition(sprintf "%s:%s" subsystemId.ObjectId subsystem, Some baseTy, HideObjectMethods = true)
                                    subsystemTy.AddXmlDoc(sprintf "Subsystem %s of X-Road member %s (%s)." subsystem memberItem.Name memberItem.Code)
                                    subsystemTy.AddMember(ProvidedField.Literal("Name", typeof<string>, subsystem))
                                    subsystemTy.AddMember(ProvidedProperty("Identifier", typeof<XRoadMemberIdentifier>, isStatic = true, getterCode = (fun _ -> <@@ XRoadMemberIdentifier(xRoadInstance, memberClassName, memberItemCode, subsystem) @@>)))
                                    subsystemTy.AddMembersDelayed(fun _ -> addServices subsystemId)
                                    subsystemTy))
                            memberTy))
                    classTy :> MemberInfo)
            with e -> [noteProperty (e.ToString())])

        centralServicesTy.AddMembersDelayed (fun _ ->
            try
                match SecurityServerV6.downloadCentralServiceList securityServerUri xRoadInstance refresh with
                | [] -> [noteProperty "No central services are listed in this X-Road instance."]
                | services -> services |> List.map (fun serviceCode -> upcast ProvidedField.Literal(serviceCode, typeof<string>, serviceCode))
            with e -> [noteProperty (e.ToString())])

        thisTy

    let server6Parameters =
        [ ProvidedStaticParameter("SecurityServerUri", typeof<string>), "X-Road security server uri which is used to connect to that X-Road instance."
          ProvidedStaticParameter("XRoadInstance", typeof<string>), "Code identifying the instance of X-Road system."
          ProvidedStaticParameter("MemberClass", typeof<string>), "Member class that is used in client identifier in X-Road request."
          ProvidedStaticParameter("MemberCode", typeof<string>), "Member code that is used in client identifier in X-Road requests."
          ProvidedStaticParameter("SubsystemCode", typeof<string>, ""), "Subsystem code that is used in client identifier in X-Road requests."
          ProvidedStaticParameter("ForceRefresh", typeof<bool>, false), "When `true`, forces type provider to refresh data from security server." ]
        |> List.map (fun (parameter,doc) -> parameter.AddXmlDoc(doc); parameter)

    // Generic type for collecting information from selected X-Road instance.
    let server6Ty =
        let typ = ProvidedTypeDefinition(theAssembly, namespaceName, "XRoadServer6", Some baseTy)
        typ.AddXmlDoc("Type provider which collects data from selected X-Road instance.")
        typ

    do server6Ty.DefineStaticParameters(server6Parameters, buildServer6Types)
    do this.AddNamespace(namespaceName, [serverTy; server6Ty])
