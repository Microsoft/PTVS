using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.PythonTools.Infrastructure {
    internal abstract class ComDisposerBase : IDisposable {
        protected System.Collections.Generic.List<object> ObjectList { get; set; }

        protected ComDisposerBase() {
            ObjectList = new List<object>();
        }
        public void Add(object o) {
            ObjectList.Add(o);
        }
        public void AddRange(IEnumerable<object> collection) {
            ObjectList.AddRange(collection);
        }
        public void Remove(object o) {
            ObjectList.Remove(o);
        }
        void IDisposable.Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected abstract void Dispose(bool disposing);

        protected abstract void DisposeObject(object o);

        public static void ReleaseComObject(object o) {
            if (o != null && Marshal.IsComObject(o)) {
                Marshal.ReleaseComObject(o);
            }
        }

        public static void ReleaseComReference(IntPtr ptr) {
            if (ptr != IntPtr.Zero) {
                Marshal.Release(ptr);
            }
        }

#if DEBUG
        ~ComDisposerBase() {
            System.Diagnostics.Debug.Assert(false, "Unexpected finalizer call in com disposer");
        }
#endif
    }

    /// <summary>
    /// A disposer for releasing COM objects and COM references.
    /// </summary>

    internal class ComDisposer : ComDisposerBase {
        private bool _isDisposed = false;

        public ComDisposer()
            : base() {
        }

        public ComDisposer(object o)
            : base() {
            Add(o);
        }

        /// <summary>
        /// Checked version of <see cref="Add"/>. First checks to ensure that the given pointer
        /// is valid, then adds it to the dispose list.
        /// </summary>
        /// <param name="ptr">The pointer to add.</param>
        /// <exception cref="ObjectDisposedException">If this disposer is already disposed.</exception>
        /// <exception cref="ArgumentException">If the given pointer is null.</exception>
        public void AddReference(IntPtr ptr) {
            if (_isDisposed) {
                throw new ObjectDisposedException("ComDisposer");
            }

            if (ptr == IntPtr.Zero) {
                throw new ArgumentException("Invalid ptr", "ptr");
            }
            base.Add(ptr);
        }

        /// <summary>
        /// Checked version of <see cref="Add"/>. First checks to ensure that the given object
        /// is a com object, then adds it to the dispose list.
        /// </summary>
        /// <param name="o">The com object to add.</param>
        /// <exception cref="ObjectDisposedException">If this disposer is already disposed.</exception>
        /// <exception cref="ArgumentException">If <paramref name="o"/> is not a COM object.</exception>
        public void AddComObject(object o) {
            if (_isDisposed) {
                throw new ObjectDisposedException("ComDisposer");
            }
            if (o == null) {
                throw new ArgumentNullException("o");
            }
            if (!Marshal.IsComObject(o)) {
                throw new ArgumentOutOfRangeException("o");
            }

            base.Add(o);
        }

        /// <summary>
        /// Returns a ComDisposer with <paramref name="comObjects"/> added to the it to be dispose.
        /// </summary>
        public static ComDisposer GetComDisposer(params object[] comObjects) {
            ComDisposer comDisposer = new ComDisposer();
            comDisposer.AddRange(comObjects);
            return comDisposer;
        }

        public static void DisposeComObjects(params object[] comObjects) {
            using (ComDisposer comDisposer = GetComDisposer(comObjects.Where(o => o != null))) {
            }
        }

        protected sealed override void Dispose(bool disposing) {
            if (disposing && (ObjectList != null) && (!_isDisposed)) {
                foreach (object o in ObjectList) {
                    DisposeObject(o);
                }

                ObjectList = null;
            }
        }

        protected override void DisposeObject(object o) {
            try {
                if (o is IntPtr) {
                    ReleaseComReference((IntPtr)o);
                }

                ReleaseComObject(o);
            } catch { System.Diagnostics.Debug.Assert(false, "Could not release COM reference"); } // Dispose should never throw.
        }
    }

    /// <summary>
    /// Disposes IDisposable objects or COM objects
    /// </summary>
    internal sealed class HybridDisposer : ComDisposer {
        protected override void DisposeObject(object o) {
            var disposable = o as IDisposable;
            if (disposable != null) {
                disposable.Dispose();
            } else {
                base.DisposeObject(o);
            }
        }
    }
}
