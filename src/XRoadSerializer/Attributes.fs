﻿namespace XRoad.Attributes

open System

type LayoutKind =
    | All = 0
    | Choice = 1
    | Sequence = 2

[<AttributeUsage(AttributeTargets.Class)>]
type XRoadTypeAttribute(layout: LayoutKind) =
    inherit Attribute()
    member val Layout = layout with get

[<AttributeUsage(AttributeTargets.Property)>]
type XRoadElementAttribute() =
    inherit Attribute()