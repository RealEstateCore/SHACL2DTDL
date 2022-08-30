using VDS.RDF;
using VDS.RDF.Ontology;
using DotNetRdfExtensions;
using DotNetRdfExtensions.SHACL;

namespace SHACL2DTDL
{
    class Property {


        public enum PropertyType {
            Data,
            Object
        }

        public IUriNode WrappedProperty { get; }
        public IUriNode? Target { get; }
        public IEnumerable<INode> In { get; }
        public int? MaxCardinality { get; set; }
        public int? MinCardinality { get; set; }
        public PropertyType Type { get; set; }
        public IEnumerable<ILiteralNode> Labels { get; init; }
        public IEnumerable<ILiteralNode> Comments { get; init; }

        public string Name {
            get {
                return WrappedProperty.LocalName();
            }
        }

        // TODO: Support different types of property paths, see https://www.w3.org/TR/shacl/#property-paths 
        // (currently we only support simple ) predicate paths, i.e., where ps.Path.NodeType = NodeType.Uri
        public Property(PropertyShape pShape) {
            // Set WrappedProperty
            if (pShape.Path is not IUriNode) {
                throw new ArgumentException($"Property path {pShape.Path} is not a URI node");
            }
            WrappedProperty = (IUriNode)pShape.Path;

            // Set up labels and comments from property shape or (fallback) from wrapped property label/comment
            if (pShape.Names.Count() > 0) {
                Labels = pShape.Names;
            }
            else {
                Labels = pShape.Path.RdfsLabels();
            }
            if (pShape.Descriptions.Count() > 0) {
                Comments = pShape.Descriptions;
            }
            else {
                Comments = pShape.Path.RdfsComments();
            }

            // Set Type & Target
            if (pShape.Datatype is IUriNode || (pShape.NodeKind is IUriNode nodeKind1 && nodeKind1.LocalName() == "Literal")) {
                Type = PropertyType.Data;
                // Note: pShape.Datatype can be null
                Target = pShape.Datatype;
            }
            else if (pShape.Class.FirstOrDefault() is IUriNode || pShape.NodeKind is IUriNode nodeKind2 && nodeKind2.LocalName() == "IRI") {
                Type = PropertyType.Object;
                // Note: pShape.Class.FirstOrDefault() can be null
                Target = pShape.Class.FirstOrDefault();
            }
            else {
                Type = PropertyType.Object;
            }

            In = pShape.In;

            // Set cardinality
            // Note: both of these can be null
            MinCardinality = pShape.MinCount;
            MaxCardinality = pShape.MaxCount;
        }

        public Property(OntologyProperty property) {
            // Set WrappedProperty
            if (property.Resource is not IUriNode) {
                throw new ArgumentException($"Property path {property.Resource} is not a URI node");
            }
            WrappedProperty = (IUriNode)property.Resource;
            Labels = property.Resource.RdfsLabels();
            Comments = property.Resource.RdfsComments();

            In = new List<INode>();
            // Set Target if singleton URI exists
            if (property.Ranges.Count() == 1) {
                if (property.Ranges.First().Resource is IUriNode) {
                    OntologyClass range = property.Ranges.First();
                    IUriNode rangeNode = (IUriNode)range.Resource;
                    Target = rangeNode;
                }
                if (property.Ranges.First().IsEnumerationDatatype()) {
                    In = property.Ranges.First().AsEnumeration();
                }
            }

            // If we are declared to be an OWL datatype property or if our range is explicitly a XSD, then we are a data property
            if (WrappedProperty.TransitiveRdfTypes().Any(type => type.LocalName() == "DatatypeProperty") || (Target is not null && Target.IsXsdType())) {
                Type = PropertyType.Data;
            }
            else {
                // Otherwise, we're an object property
                Type = PropertyType.Object;
            }

            // Set cardinality
            if (WrappedProperty.TransitiveRdfTypes().Any(type => type.LocalName() == "FunctionalProperty")) {
                MinCardinality = MaxCardinality = 1;
            }
        }

        public class PropertyNameComparer: IEqualityComparer<Property>
        {
            public bool Equals(Property? one, Property? two) {
                if (one is null || two is null) {
                    return false;
                }
                return StringComparer.InvariantCultureIgnoreCase.Equals(one.Name, two.Name);
            }

            public int GetHashCode(Property item) {
                return StringComparer.InvariantCultureIgnoreCase.GetHashCode(item.Name);
            }
        }
    }
}
