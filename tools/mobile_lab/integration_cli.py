from __future__ import annotations

import argparse
import json
import os

from .integration_fabric import (
    GitHubPublicApiClient,
    LocalToolProbe,
    MOBILE_TOOL_PROBES,
    OSVApiClient,
    integration_inventory,
)


def _emit(value) -> None:
    if hasattr(value, "__dict__"):
        value = value.__dict__
    print(json.dumps(value, indent=2, sort_keys=True, default=lambda o: o.__dict__))


def main() -> int:
    parser = argparse.ArgumentParser(description="AEVRIX Mobile Integration Fabric")
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("inventory", help="Print governed integration candidate inventory")
    sub.add_parser("probe-local", help="Discover/fingerprint supported local toolchains")

    gh_release = sub.add_parser("github-releases", help="Read public GitHub release metadata")
    gh_release.add_argument("owner")
    gh_release.add_argument("repo")

    gh_sbom = sub.add_parser("github-sbom", help="Read a public repository SPDX SBOM")
    gh_sbom.add_argument("owner")
    gh_sbom.add_argument("repo")

    osv = sub.add_parser("osv-package", help="Query OSV by package/version")
    osv.add_argument("ecosystem")
    osv.add_argument("name")
    osv.add_argument("version", nargs="?")

    args = parser.parse_args()
    if args.command == "inventory":
        _emit(integration_inventory())
        return 0
    if args.command == "probe-local":
        probe = LocalToolProbe()
        _emit({"tools": [probe.probe(spec).__dict__ for spec in MOBILE_TOOL_PROBES]})
        return 0
    if args.command == "github-releases":
        result = GitHubPublicApiClient(token=os.getenv("GITHUB_TOKEN")).releases(args.owner, args.repo)
        _emit({"data": result.data, "evidence": result.evidence.__dict__})
        return 0
    if args.command == "github-sbom":
        result = GitHubPublicApiClient(token=os.getenv("GITHUB_TOKEN")).dependency_sbom(args.owner, args.repo)
        _emit({"data": result.data, "evidence": result.evidence.__dict__})
        return 0
    if args.command == "osv-package":
        result = OSVApiClient().query_package(args.name, args.ecosystem, args.version)
        _emit({"data": result.data, "evidence": result.evidence.__dict__})
        return 0
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
