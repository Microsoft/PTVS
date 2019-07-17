# Python Tools for Visual Studio
# Copyright(c) Microsoft Corporation
# All rights reserved.
# 
# Licensed under the Apache License, Version 2.0 (the License); you may not use
# this file except in compliance with the License. You may obtain a copy of the
# License at http://www.apache.org/licenses/LICENSE-2.0
# 
# THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
# OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
# IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
# MERCHANTABILITY OR NON-INFRINGEMENT.
# 
# See the Apache Version 2.0 License for specific language governing
# permissions and limitations under the License.

import os
import sys
import traceback

def main():
    cwd, testRunner, secret, port, debugger_search_path, args = parse_argv()
    load_debugger(cwd, secret, port, debugger_search_path)
    run(testRunner, args)

def parse_argv():
    """Parses arguments for use with the test launcher.
    Arguments are:
    1. Working directory.
    2. Test runner, `pytest` or `nose`
    3. debugSecret
    4. debugPort
    5. Debugger path
    6. Rest of the arguments are passed into the test runner.
    """

    return (sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5], sys.argv[6:])

def load_debugger(cwd, secret, port, debugger_search_path):
    # Load the debugger package
    try:
        sys.path[0] = os.getcwd()
        os.chdir(cwd)

        if debugger_search_path:
            sys.path.append(debugger_search_path)
        
        if secret and port:
            # Start tests with legacy debugger
            import ptvsd
            from ptvsd.debugger import DONT_DEBUG, DEBUG_ENTRYPOINTS, get_code
            from ptvsd import enable_attach, wait_for_attach

            DONT_DEBUG.append(os.path.normcase(__file__))
            DEBUG_ENTRYPOINTS.add(get_code(main))
            enable_attach(secret, ('127.0.0.1', port), redirect_output = True)
            wait_for_attach()
        elif port:
            # Start tests with new debugger
            from ptvsd import enable_attach, wait_for_attach
            
            enable_attach(('127.0.0.1', port), redirect_output = True)
            wait_for_attach()
    except:
        traceback.print_exc()
        print('''
Internal error detected. Please copy the above traceback and report at
https://github.com/Microsoft/vscode-python/issues/new

Press Enter to close. . .''')
        try:
            raw_input()
        except NameError:
            input()
        sys.exit(1)

def run(testRunner, args):
    """Runs the test
    cwd -- the current directory to be set
    testRunner -- test runner to be used `pytest` or `nose`
    args -- arguments passed into the test runner
    """

    try:
        if testRunner == 'pytest':
            import pytest
            pytest.main(args)
        else:
            import nose
            nose.run(argv=args)
        sys.exit(0)
    finally:
        pass

if __name__ == '__main__':
    main()