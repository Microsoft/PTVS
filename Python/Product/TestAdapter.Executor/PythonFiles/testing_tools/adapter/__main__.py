# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License.

from __future__ import absolute_import

import argparse
import sys

from . import pytest, report, unittest
from .errors import UnsupportedToolError, UnsupportedCommandError

TOOLS = {
    'pytest': {
        '_add_subparser': pytest.add_cli_subparser,
        'discover': pytest.discover,
        },
    'unittest': {
        '_add_subparser': unittest.add_unittest_cli_subparser,
        'discover': unittest.discover,
        },
    }
REPORTERS = {
    'pytest': {
        'discover': report.report_discovered,
        },
    'unittest': {
        'discover': report.report_unittest_discovered
        }
    }



def parse_args(
        argv=sys.argv[1:],
        prog=sys.argv[0],
        ):
    """
    Return the subcommand & tool to run, along with its args.

    This defines the standard CLI for the different testing frameworks.
    """
    parser = argparse.ArgumentParser(
            description='Run Python testing operations.',
            prog=prog,
            )
    cmdsubs = parser.add_subparsers(dest='cmd')

    # Add "run" and "debug" subcommands when ready.
    for cmdname in ['discover']:
        sub = cmdsubs.add_parser(cmdname)
        subsubs = sub.add_subparsers(dest='tool')
        for toolname in sorted(TOOLS):
            try:
                add_subparser = TOOLS[toolname]['_add_subparser']
            except KeyError:
                continue
            subsub = add_subparser(cmdname, toolname, subsubs)
            if cmdname == 'discover':
                subsub.add_argument('--simple', action='store_true')
                subsub.add_argument('--no-hide-stdio', dest='hidestdio',
                                    action='store_false')
                subsub.add_argument('--pretty', action='store_true')

    # Parse the args!
    if '--' in argv:
        seppos = argv.index('--')
        toolargs = argv[seppos + 1:]
        argv = argv[:seppos]
    else:
        toolargs = []
    args = parser.parse_args(argv)
    ns = vars(args)

    cmd = ns.pop('cmd')
    if not cmd:
        parser.error('missing command')

    tool = ns.pop('tool')
    if not tool:
        parser.error('missing tool')

    return tool, cmd, ns, toolargs


def main(toolname, cmdname, subargs, toolargs,
         _tools=TOOLS, _reporters=REPORTERS):
    try:
        tool = _tools[toolname]
    except KeyError:
        raise UnsupportedToolError(toolname)

    try:
        run = tool[cmdname]
        report_result = _reporters[toolname][cmdname]
    except KeyError:
        raise UnsupportedCommandError(cmdname)

    parents, result = run(toolargs, **subargs)
    report_result(result, parents,
                  **subargs
                  )


if __name__ == '__main__':
    tool, cmd, subargs, toolargs = parse_args()
    main(tool, cmd, subargs, toolargs)
