using VDS.RDF;
using VDS.RDF.Shacl;
using VDS.RDF.Nodes;

namespace DotNetRdfExtensions.SHACL
{
    public class Shape: IEquatable<Shape>  {
        /// <summary>
        /// The Node which this Shape is a wrapper around.
        /// </summary>
        protected INode _node;
        /// <summary>
        /// The Graph from which this Shape originates.
        /// </summary>
        protected ShapesGraph _graph;


        // Implementation of IEquatable<T> interface
        public bool Equals(Shape? shape)
        {
            return this.Node == shape?.Node;
        }

        public IUriNode? NodeKind {
            get {
                IUriNode shNodeKind = _graph.CreateUriNode(SH.nodeKind);
                return _graph.GetTriplesWithSubjectPredicate(_node, shNodeKind).Select(trip => trip.Object).UriNodes().FirstOrDefault();
            }
        }

        protected internal Shape(INode node, ShapesGraph graph) {
            _node = node;
            _graph = graph;
        }

        public INode Node
        {
            get
            {
                return _node;
            }
        }

        public ShapesGraph Graph
        {
            get
            {
                return _graph;
            }
        }

        public bool IsDeprecated 
        {
            get 
            {
                IUriNode owlDeprecated = _graph.CreateUriNode(OWL.deprecated);
                return _graph.GetTriplesWithSubjectPredicate(_node, owlDeprecated).Objects().LiteralNodes().Any(deprecationNode => deprecationNode.AsValuedNode().AsBoolean() == true);
            }
        }
    }
}