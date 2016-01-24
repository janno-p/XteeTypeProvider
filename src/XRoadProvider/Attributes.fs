﻿namespace XRoad.Serialization.Attributes

open System

type LayoutKind =
    | All = 0
    | Choice = 1
    | Sequence = 2

type XRoadTypeAttribute(name: string, layout: LayoutKind) =
    inherit Attribute()
    new(layout) = XRoadTypeAttribute("", layout)
    member val Layout = layout with get
    member val Name = name with get
    member val Namespace = "" with get, set

type XRoadElementAttribute(name: string) =
    inherit Attribute()
    new() = XRoadElementAttribute("")
    member val IsNullable = false with get, set
    member val Name = name with get
    member val Namespace = "" with get, set
    member val MergeContent = false with get, set
    member val UseXop = false with get, set
    member val TypeName = "" with get, set
    member val TypeNamespace = "" with get, set

type XRoadChoiceOptionAttribute(id: int, name: string) =
    inherit Attribute()
    member val Id = id with get
    member val Name = name with get
    member val MergeContent = false with get, set

type XRoadCollectionAttribute(itemName: string) =
    inherit Attribute()
    new() = XRoadCollectionAttribute("")
    member val ItemName = itemName with get
    member val ItemNamespace = "" with get, set
    member val ItemIsNullable = false with get, set
    member val MergeContent = false with get, set
