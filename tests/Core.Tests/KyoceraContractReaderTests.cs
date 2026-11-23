using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Serialization;
using ScanRelay.KyoceraProbe;

namespace ScanRelay.Core.Tests
{
    public class KyoceraContractReaderTests
    {
        [Fact]
        public void The_shape_of_a_message_is_read_from_a_program_file_without_running_it()
        {
            using var stream = File.OpenRead(typeof(KyoceraContractReaderTests).Assembly.Location);
            using var pe = new PEReader(stream);

            var text = string.Join("\n", ContractReader.Describe(pe.GetMetadataReader()));

            Assert.Contains("type Fake.address_book.add_personal_addressRequest", text);
            Assert.Contains("Fake.address_book.PERSONAL_ADDRESS personal_addressField", text);
            Assert.Contains("[XmlElement(Order=0)]", text);
            Assert.Contains("type Fake.address_book.PERSONAL_ADDRESS", text);
            Assert.Contains("type Fake.address_book.SMB_DETAIL", text);
            Assert.Contains("[XmlElement(\"login_password\", Order=1)]", text);
            Assert.Contains("enum Fake.address_book.ADD_RESULT", text);
            Assert.Contains("    SUCCESS", text);
            Assert.Contains("    MEMORY_FULL_ERROR", text);
            Assert.DoesNotContain("UnrelatedThing", text);
        }
    }
}

// Stand-ins shaped like a generated SOAP proxy, for the reader to find.
namespace Fake.address_book
{
#pragma warning disable CS0649, CA1707, IDE1006, CS8618
    public class add_personal_addressRequest
    {
        [XmlElement(Order = 0)] public PERSONAL_ADDRESS personal_addressField;
    }

    public class PERSONAL_ADDRESS
    {
        public SMB_DETAIL[] smbField;
        public ADD_RESULT lastResult;
    }

    public class SMB_DETAIL
    {
        [XmlElement("login_password", Order = 1)] public string passwordField;
    }

    public enum ADD_RESULT
    {
        SUCCESS,
        MEMORY_FULL_ERROR,
    }

    public class UnrelatedThing
    {
        public int value;
    }
#pragma warning restore CS0649, CA1707, IDE1006, CS8618
}
