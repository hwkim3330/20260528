namespace EthernetPacketGenerator.Models;

public enum ProtocolType
{
    Ethernet,
    ARP,
    IPv4,
    IPv6,
    ICMP,
    TCP,
    UDP,
    VLAN,
    RawPayload
}

public enum EtherTypeValue : ushort
{
    IPv4  = 0x0800,
    ARP   = 0x0806,
    VLAN  = 0x8100,
    IPv6  = 0x86DD,
    Unknown = 0x0000
}

public enum IpProtocolValue : byte
{
    ICMP = 1,
    TCP  = 6,
    UDP  = 17
}

public enum ValidationSeverity
{
    None,
    Warning,
    Error
}

public enum ArpOperation : ushort
{
    Request = 1,
    Reply   = 2
}

public enum IcmpType : byte
{
    EchoReply              = 0,
    DestinationUnreachable = 3,
    EchoRequest            = 8,
    TimeExceeded           = 11
}
