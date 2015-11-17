﻿module XRoadSerializer.Tests.Serialization

open FsUnit
open NUnit.Framework
open System
open System.IO
open System.Text
open System.Xml
open XRoad
open XRoad.Attributes

module TestType =
    type UnserializableType() =
        member val Value = 10 with get, set

    [<XRoadType(LayoutKind.Sequence)>]
    type WithContent() =
        [<XRoadContent>]
        member val ContentValue = true with get, set

    [<XRoadType(LayoutKind.Sequence)>]
    type ComplexType() =
        [<XRoadElement>]
        member val String = "test" with get, set
        [<XRoadElement>]
        member val BigInteger = 100I with get, set

    [<XRoadType(LayoutKind.Sequence)>]
    type SimpleType() =
        [<XRoadElement>]
        member val Value = 13 with get, set
        [<XRoadElement>]
        member val ComplexValue = ComplexType() with get, set
        [<XRoadElement>]
        member val SubContent = WithContent() with get, set
        member val IgnoredValue = true with get, set

    [<XRoadType(LayoutKind.Sequence)>]
    type WithNullableMembers() =
        [<XRoadElement(IsNullable=true)>]
        member val Value1 = Nullable(13) with get, set
        [<XRoadElement(IsNullable=true)>]
        member val Value2 = Nullable() with get, set

let serialize qn value =
    let serializer = Serializer()
    use stream = new MemoryStream()
    use sw = new StreamWriter(stream, Encoding.UTF8)
    use writer = XmlWriter.Create(sw)
    writer.WriteStartDocument()
    writer.WriteStartElement("wrapper")
    writer.WriteAttributeString("xmlns", "xsi", XmlNamespace.Xmlns, XmlNamespace.Xsi)
    serializer.Serialize(writer, value, qn)
    writer.WriteEndElement()
    writer.WriteEndDocument()
    writer.Flush()
    sw.Flush()
    stream.Position <- 0L
    use sr = new StreamReader(stream, Encoding.UTF8)
    sr.ReadToEnd()

let serialize' v = serialize (XmlQualifiedName("keha")) v

let [<Test>] ``initializes new serializer`` () =
    let serializer = Serializer()
    serializer |> should not' (equal null)

let [<Test>] ``can serialize simple value`` () =
    let result = TestType.SimpleType() |> serialize'
    result |> should equal @"<?xml version=""1.0"" encoding=""utf-8""?><wrapper xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""><keha><Value>13</Value><ComplexValue><String>test</String><BigInteger>100</BigInteger></ComplexValue><SubContent>true</SubContent></keha></wrapper>"

let [<Test>] ``serialize null value`` () =
    let result = (null: string) |> serialize'
    result |> should equal @"<?xml version=""1.0"" encoding=""utf-8""?><wrapper xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""><keha xsi:nil=""true"" /></wrapper>"

let [<Test>] ``write qualified root name`` () =
    let result = TestType.SimpleType() |> serialize (XmlQualifiedName("root", "urn:some-namespace"))
    result |> should equal @"<?xml version=""1.0"" encoding=""utf-8""?><wrapper xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""><root xmlns=""urn:some-namespace""><Value>13</Value><ComplexValue><String>test</String><BigInteger>100</BigInteger></ComplexValue><SubContent>true</SubContent></root></wrapper>"

let [<Test>] ``serializing unserializable type`` () =
    TestDelegate(fun _ -> TestType.UnserializableType() |> serialize' |> ignore)
    |> should (throwWithMessage "Type `XRoadSerializer.Tests.Serialization+TestType+UnserializableType` is not serializable.") typeof<Exception>

let [<Test>] ``serialize string value`` () =
    "string value" |> serialize' |> should equal @"<?xml version=""1.0"" encoding=""utf-8""?><wrapper xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""><keha>string value</keha></wrapper>"

let [<Test>] ``serialize integer value`` () =
    32 |> serialize' |> should equal @"<?xml version=""1.0"" encoding=""utf-8""?><wrapper xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""><keha>32</keha></wrapper>"

let [<Test>] ``serialize nullable values`` () =
    let result = TestType.WithNullableMembers() |> serialize'
    result |> should equal @"<?xml version=""1.0"" encoding=""utf-8""?><wrapper xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""><keha><Value1>13</Value1><Value2 xsi:nil=""true"" /></keha></wrapper>"
