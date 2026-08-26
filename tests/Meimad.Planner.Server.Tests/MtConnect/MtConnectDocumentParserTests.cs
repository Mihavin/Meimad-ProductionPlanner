using Meimad.Planner.Server.Infrastructure.MtConnect;

namespace Meimad.Planner.Server.Tests.MtConnect;

public sealed class MtConnectDocumentParserTests
{
    [Fact]
    public void Probe_parser_handles_default_1_2_namespace_and_extracts_device_identity()
    {
        var result = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);

        Assert.Equal(DateTimeOffset.Parse("2026-08-23T17:23:01.452Z"), result.Header.CreationTime);
        Assert.Equal("NGC", result.Header.Sender);
        Assert.Equal("1787493414", result.Header.InstanceId);
        Assert.Equal("1.2.0.1.2", result.Header.Version);
        Assert.Equal(333, result.Header.BufferSize);
        var device = Assert.Single(result.Devices);
        Assert.Equal("dev1", device.Id);
        Assert.Equal("VF-3SS", device.Name);
        Assert.Equal("000", device.Uuid);
        Assert.Equal(MtConnectTestDocuments.Probe, result.RawXml);

        var macroRange = Assert.Single(result.DataItems,
            value => value.Id == "macrorange5");
        Assert.Equal("MacroRange5", macroRange.Name);
        Assert.Equal("MESSAGE", macroRange.Type);
        Assert.Equal("EVENT", macroRange.Category);
        Assert.Equal("Macros 10600 to 10799", macroRange.Source);
    }

    [Fact]
    public void Current_parser_handles_prefixed_1_2_namespace_and_extracts_useful_observations()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var result = MtConnectDocumentParser.ParseCurrent(MtConnectTestDocuments.Current, probe);

        Assert.Equal(21246, result.Header.FirstSequence);
        Assert.Equal(21578, result.Header.LastSequence);
        Assert.Equal(21579, result.Header.NextSequence);
        var device = Assert.Single(result.Devices);
        Assert.Null(device.Identity.Id);
        Assert.Equal("VF-3SS", device.Identity.Name);
        Assert.Equal("000", device.Identity.Uuid);
        Assert.Equal("AVAILABLE", device.Availability?.Value);
        Assert.Equal("AUTOMATIC", device.ControllerMode?.Value);
        Assert.Equal("ACTIVE", device.Execution?.Value);
        Assert.Equal(15328, device.Execution?.Sequence);
        Assert.Equal("1500.CNC", device.Program?.Value);
        Assert.Equal(13, device.Observations.Count);
        Assert.Equal(MtConnectTestDocuments.Current, result.RawXml);

        var spindle = Assert.Single(device.Observations,
            value => value.DataItemId == "spindle-speed");
        Assert.Equal("SpindleSpeed", spindle.ElementName);
        Assert.Equal("SPINDLE_SPEED", spindle.Definition?.Type);
        Assert.Equal("12000", spindle.Value);
        var alarm = Assert.Single(device.Observations,
            value => value.DataItemId == "mcond");
        Assert.Equal("Fault", alarm.ElementName);
        Assert.Equal("123", alarm.Attributes["nativeCode"]);
        Assert.Equal("HIGH", alarm.Attributes["nativeSeverity"]);

        Assert.Collection(device.Counters,
            counter =>
            {
                Assert.Equal("m30c1", counter.Observation.DataItemId);
                Assert.Equal("M30Counter1", counter.Observation.Name);
                Assert.Equal(9300, counter.NumericValue);
            },
            counter =>
            {
                Assert.Equal("m30c2", counter.Observation.DataItemId);
                Assert.Equal("M30Counter2", counter.Observation.Name);
                Assert.Null(counter.NumericValue);
            },
            counter =>
            {
                Assert.Equal("part-count", counter.Observation.DataItemId);
                Assert.Equal(42, counter.NumericValue);
            });

        var temporaryMacro = Assert.Single(device.MacroVariables,
            value => value.VariableNumber == 10605);
        Assert.Equal(1m, temporaryMacro.NumericValue);
        Assert.Equal("1.0", temporaryMacro.RawValue);
        Assert.Equal("Macros 10600 to 10799",
            temporaryMacro.RangeObservation.Definition?.Source);
        var unavailableVariable = Assert.Single(device.MacroVariables,
            value => value.VariableNumber == 10606);
        Assert.Null(unavailableVariable.NumericValue);
        Assert.Equal("NaN", unavailableVariable.RawValue);
    }

    [Fact]
    public void Current_parser_uses_the_highest_sequence_for_repeated_observations_and_counters()
    {
        var probe = MtConnectDocumentParser.ParseProbe(MtConnectTestDocuments.Probe);
        var result = MtConnectDocumentParser.ParseCurrent(MtConnectTestDocuments.Current, probe);
        var device = Assert.Single(result.Devices);

        Assert.Equal("ACTIVE", device.Execution?.Value);
        Assert.Equal(9300, device.Counters[0].NumericValue);
    }

    [Fact]
    public void Parser_surfaces_MTConnect_error_documents_returned_with_HTTP_200()
    {
        var exception = Assert.Throws<MtConnectProtocolException>(() =>
            MtConnectDocumentParser.ParseCurrent(MtConnectTestDocuments.Error));

        Assert.Contains("INVALID_XPATH", exception.Message, StringComparison.Ordinal);
        Assert.Contains("path could not be parsed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_rejects_an_unexpected_namespace()
    {
        const string xml = """
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.0">
              <Header creationTime="2026-08-23T17:23:01Z" />
              <Streams />
            </MTConnectStreams>
            """;

        var exception = Assert.Throws<MtConnectProtocolException>(() =>
            MtConnectDocumentParser.ParseCurrent(xml));

        Assert.Contains("MTConnect 1.2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_rejects_malformed_XML_as_a_protocol_error()
    {
        var exception = Assert.Throws<MtConnectProtocolException>(() =>
            MtConnectDocumentParser.ParseProbe("<MTConnectDevices>"));

        Assert.IsType<System.Xml.XmlException>(exception.InnerException);
    }

    [Fact]
    public async Task Async_parser_rejects_a_response_larger_than_the_bounded_limit()
    {
        var bytes = new byte[MtConnectDocumentParser.MaximumDocumentCharacters + 1];
        Array.Fill(bytes, (byte)'x');
        await using var stream = new MemoryStream(bytes, writable: false);

        var exception = await Assert.ThrowsAsync<MtConnectProtocolException>(() =>
            MtConnectDocumentParser.ParseProbeAsync(stream));

        Assert.Contains("exceeded", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class MtConnectTestDocuments
{
    internal const string Probe = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MTConnectDevices xmlns:m="urn:mtconnect.org:MTConnectDevices:1.2"
                          xmlns="urn:mtconnect.org:MTConnectDevices:1.2">
          <Header creationTime="2026-08-23T17:23:01.452Z"
                  sender="NGC"
                  instanceId="1787493414"
                  version="1.2.0.1.2"
                  bufferSize="333" />
          <Devices>
            <Device id="dev1" name="VF-3SS" uuid="000">
              <DataItems>
                <DataItem id="avail" name="Availability" type="AVAILABILITY" category="EVENT" />
                <DataItem id="mode" name="Mode" type="CONTROLLER_MODE" category="EVENT" />
                <DataItem id="rstat" name="RunStatus" type="EXECUTION" category="EVENT" />
                <DataItem id="ncprog" name="Program" type="PROGRAM" category="EVENT" />
                <DataItem id="m30c1" name="M30Counter1" type="MESSAGE" category="EVENT">
                  <Source>M30 Counter #1</Source>
                </DataItem>
                <DataItem id="m30c2" name="M30Counter2" type="MESSAGE" category="EVENT">
                  <Source>M30 Counter #2</Source>
                </DataItem>
                <DataItem id="macrorange5" name="MacroRange5" type="MESSAGE" category="EVENT">
                  <Source>Macros 10600 to 10799</Source>
                </DataItem>
                <DataItem id="spindle-speed" name="SpindleSpeed" type="SPINDLE_SPEED" category="SAMPLE" />
                <DataItem id="path-feed" name="PathFeedrate" type="PATH_FEEDRATE" category="SAMPLE" />
                <DataItem id="mcond" name="MachineCondition" type="SYSTEM" category="CONDITION" />
              </DataItems>
            </Device>
          </Devices>
        </MTConnectDevices>
        """;

    internal const string Current = """
        <?xml version="1.0" encoding="UTF-8"?>
        <m:MTConnectStreams xmlns:m="urn:mtconnect.org:MTConnectStreams:1.2">
          <m:Header creationTime="2026-08-23T17:23:01.481Z"
                    sender="NGC"
                    instanceId="1787493414"
                    version="1.2.0.1.2"
                    bufferSize="333"
                    firstSequence="21246"
                    lastSequence="21578"
                    nextSequence="21579" />
          <m:Streams>
            <m:DeviceStream name="VF-3SS" uuid="000">
              <m:ComponentStream component="Device" componentId="dev1" name="VF-3SS">
                <m:Events>
                  <m:Availability dataItemId="avail" name="Availability" sequence="1"
                                  timestamp="2026-08-23T13:56:58.378Z">AVAILABLE</m:Availability>
                  <m:ControllerMode dataItemId="mode" name="Mode" sequence="15322"
                                    timestamp="2026-08-23T16:57:57.807Z">AUTOMATIC</m:ControllerMode>
                  <m:Execution dataItemId="rstat" name="RunStatus" sequence="15320"
                               timestamp="2026-08-23T16:57:50.000Z">STOPPED</m:Execution>
                  <m:Execution dataItemId="rstat" name="RunStatus" sequence="15328"
                               timestamp="2026-08-23T16:58:05.809Z">ACTIVE</m:Execution>
                  <m:Program dataItemId="ncprog" name="Program" sequence="156"
                             timestamp="2026-08-23T13:57:00.489Z">1500.CNC</m:Program>
                  <m:Message dataItemId="m30c1" sequence="8320"
                             timestamp="2026-08-23T15:18:00.000Z">9299</m:Message>
                  <m:Message dataItemId="m30c1" sequence="8328"
                             timestamp="2026-08-23T15:19:29.578Z">9300</m:Message>
                  <m:Message dataItemId="m30c2" sequence="8329"
                             timestamp="2026-08-23T15:19:29.578Z">UNAVAILABLE</m:Message>
                  <m:PartCount dataItemId="part-count" name="CompletedParts" sequence="8330"
                               timestamp="2026-08-23T15:19:30.000Z">42</m:PartCount>
                  <m:Message dataItemId="macrorange5" name="MacroRange5" sequence="8331"
                             timestamp="2026-08-23T15:19:31.000Z">0.0,1.0,2.0,3.0,4.0,1.0,NaN</m:Message>
                </m:Events>
                <m:Samples>
                  <m:SpindleSpeed dataItemId="spindle-speed" name="SpindleSpeed" sequence="8332"
                                  timestamp="2026-08-23T15:19:32.000Z">12000</m:SpindleSpeed>
                  <m:PathFeedrate dataItemId="path-feed" name="PathFeedrate" sequence="8333"
                                  timestamp="2026-08-23T15:19:33.000Z">800.5</m:PathFeedrate>
                </m:Samples>
                <m:Condition>
                  <m:Fault dataItemId="mcond" name="MachineCondition" sequence="8334"
                           timestamp="2026-08-23T15:19:34.000Z"
                           nativeCode="123" nativeSeverity="HIGH">Door interlock</m:Fault>
                </m:Condition>
              </m:ComponentStream>
            </m:DeviceStream>
          </m:Streams>
        </m:MTConnectStreams>
        """;

    internal const string Error = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MTConnectError xmlns="urn:mtconnect.org:MTConnectError:1.2">
          <Header creationTime="2026-08-23T17:23:01.481Z"
                  sender="NGC" instanceId="1787493414" version="1.2.0.1.2" />
          <Errors>
            <Error errorCode="INVALID_XPATH">The path could not be parsed.</Error>
          </Errors>
        </MTConnectError>
        """;
}
