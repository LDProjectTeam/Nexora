use anyhow::Result;
use librqbit::{AddTorrent, AddTorrentOptions, AddTorrentResponse, Session, SessionOptions};
use std::path::PathBuf;

#[tokio::main]
async fn main() -> Result<()> {
    println!("GameVault BitTorrent Engine (powered by librqbit)");
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 {
        println!("Usage: gamevault-torrent <download_dir> [torrent_file_or_magnet]");
        return Ok(());
    }

    let download_dir = PathBuf::from(&args[1]);
    let session = Session::new_with_opts(
        download_dir,
        SessionOptions {
            fastresume: true,
            ..Default::default()
        },
    )
    .await?;

    if args.len() >= 3 {
        let source = &args[2];
        println!("Adding torrent: {}", source);
        let add = if source.starts_with("magnet:") {
            AddTorrent::from_url(source)
        } else {
            let bytes = std::fs::read(source)?;
            AddTorrent::from_bytes(bytes)
        };

        let resp = session.add_torrent(add, Some(AddTorrentOptions {
            overwrite: true,
            ..Default::default()
        })).await?;

        match resp {
            AddTorrentResponse::Added(id, handle) => {
                println!("Torrent added: id={}, info_hash={}", id, handle.info_hash().as_string());
            }
            AddTorrentResponse::AlreadyManaged(id, handle) => {
                println!("Torrent already managed: id={}, info_hash={}", id, handle.info_hash().as_string());
            }
            AddTorrentResponse::ListOnly(lo) => {
                println!("List only: info_hash={}", lo.info_hash.as_string());
            }
        }
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::tempdir;

    #[tokio::test]
    async fn test_session_creation_and_shutdown() -> Result<()> {
        let temp = tempdir()?;
        let session = Session::new_with_opts(
            temp.path().to_path_buf(),
            SessionOptions {
                dht: None,
                persistence: None,
                fastresume: true,
                ..Default::default()
            },
        )
        .await?;

        let count = session.with_torrents(|iter| iter.count());
        assert_eq!(count, 0);
        session.stop().await;
        Ok(())
    }

    #[tokio::test]
    async fn test_load_torrent_file_metadata() -> Result<()> {
        let torrent_path = PathBuf::from("../../test_ready.torrent");
        let actual_path = if torrent_path.exists() {
            torrent_path
        } else {
            PathBuf::from("test_ready.torrent")
        };

        if !actual_path.exists() {
            println!("test_ready.torrent not found at {:?}, skipping", actual_path);
            return Ok(());
        }

        let bytes = std::fs::read(&actual_path)?;
        assert!(!bytes.is_empty());

        let temp = tempdir()?;
        let session = Session::new_with_opts(
            temp.path().to_path_buf(),
            SessionOptions {
                dht: None,
                persistence: None,
                fastresume: true,
                ..Default::default()
            },
        )
        .await?;

        let resp = session.add_torrent(AddTorrent::from_bytes(bytes), Some(AddTorrentOptions {
            overwrite: true,
            list_only: true, // test metadata parsing without downloading
            ..Default::default()
        })).await?;

        match resp {
            AddTorrentResponse::ListOnly(lo) => {
                let name = lo.info.name();
                assert_eq!(name.as_deref().unwrap_or(""), "Ready Or Not");
                let piece_count = lo.info.info().pieces.0.len() / 20;
                assert_eq!(piece_count, 12360);
                assert_eq!(lo.info_hash.as_string(), "805a0f8d94db4015149583a42be711321d761fb5");
            }
            _ => panic!("Expected ListOnly response"),
        }

        session.stop().await;
        Ok(())
    }
}
