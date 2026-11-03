#include "TransferSession.h"

#include <algorithm>
#include <utility>

namespace mbf::twain
{

void TransferSession::Begin(
    std::uint32_t revision,
    std::vector<ScannerIpcImage>&& images,
    std::uint32_t transferBufferDelayMilliseconds)
{
    pagedImages_ = false;
    preparedPath_.clear();
    revision_ = revision;
    transferBufferDelayMilliseconds_ = transferBufferDelayMilliseconds;
    pendingImageIndex_ = 0;
    hasCurrentImage_ = false;
    currentImageIndex_ = 0;
    scanUiHidden_ = false;
    ResetMemory();
    images_ = std::move(images);
}

void TransferSession::Clear() noexcept
{
    pagedImages_ = false;
    preparedPath_.clear();
    images_.clear();
    revision_ = 0;
    transferBufferDelayMilliseconds_ = 100;
    pendingImageIndex_ = 0;
    hasCurrentImage_ = false;
    currentImageIndex_ = 0;
    scanUiHidden_ = false;
    ResetMemory();
}

void TransferSession::DiscardImages() noexcept
{
    preparedPath_.clear();
    images_.clear();
    transferBufferDelayMilliseconds_ = 100;
    pendingImageIndex_ = 0;
    hasCurrentImage_ = false;
    currentImageIndex_ = 0;
    scanUiHidden_ = false;
    ResetMemory();
}

void TransferSession::BeginPaged(std::uint32_t revision, std::uint32_t totalImages, std::uint32_t delay)
{
    Begin(revision, std::vector<ScannerIpcImage>(totalImages), delay);
    pagedImages_ = true;
}

void TransferSession::CachePage(std::uint32_t offset, const std::vector<ScannerIpcImage>& images)
{
    // Release previous batches of paths; retain the current image for the
    // host's post-transfer IMAGEINFO query.
    for (size_t index = 0; index < images_.size(); ++index)
    {
        if (index + 1U < offset) images_[index] = ScannerIpcImage{};
    }
    for (size_t index = 0; index < images.size() && offset + index < images_.size(); ++index)
        images_[offset + index] = images[index];
}

void TransferSession::CachePreparedPath(TW_UINT32 index, std::wstring path)
{
    preparedIndex_ = index;
    preparedPath_ = std::move(path);
}

const std::wstring& TransferSession::PreparedPath(TW_UINT32 index) const noexcept
{
    static const std::wstring empty;
    return preparedIndex_ == index ? preparedPath_ : empty;
}

std::uint32_t TransferSession::TakeRevision() noexcept
{
    const std::uint32_t revision = revision_;
    revision_ = 0;
    return revision;
}

bool TransferSession::HasPendingImage() const noexcept
{
    return pendingImageIndex_ < images_.size();
}

void TransferSession::LimitImages(size_t imageCount)
{
    if (images_.size() > imageCount)
    {
        images_.resize(imageCount);
        if (pendingImageIndex_ > images_.size())
        {
            pendingImageIndex_ = static_cast<TW_UINT32>(images_.size());
        }
        if (hasCurrentImage_ && currentImageIndex_ >= images_.size())
        {
            ClearCurrentImage();
        }
        if (memory_.active && memory_.imageIndex >= images_.size())
        {
            ResetMemory();
        }
    }
}

TW_UINT32 TransferSession::RemainingImageCount() const noexcept
{
    return HasPendingImage()
        ? static_cast<TW_UINT32>(images_.size() - pendingImageIndex_)
        : 0;
}

TW_UINT16 TransferSession::RemainingImageCountForTwain() const noexcept
{
    return static_cast<TW_UINT16>((std::min)(RemainingImageCount(), static_cast<TW_UINT32>(0xffff)));
}

const ScannerIpcImage& TransferSession::ImageAt(TW_UINT32 imageIndex) const noexcept
{
    return images_[imageIndex];
}

const ScannerIpcImage& TransferSession::PendingImage() const noexcept
{
    return ImageAt(pendingImageIndex_);
}

bool TransferSession::MatchesImage(TW_UINT32 imageIndex, const ScannerIpcImage& image) const noexcept
{
    return imageIndex < images_.size() &&
        (images_[imageIndex].path == image.path ||
            (!PreparedPath(imageIndex).empty() && PreparedPath(imageIndex) == image.path)) &&
        images_[imageIndex].rotationDegrees == image.rotationDegrees;
}

void TransferSession::AdvancePendingImage() noexcept
{
    ++pendingImageIndex_;
}

void TransferSession::ResetPendingImageIndex() noexcept
{
    pendingImageIndex_ = 0;
}

void TransferSession::MarkCurrentImage(TW_UINT32 imageIndex) noexcept
{
    currentImageIndex_ = imageIndex;
    hasCurrentImage_ = true;
}

void TransferSession::ClearCurrentImage() noexcept
{
    currentImageIndex_ = 0;
    hasCurrentImage_ = false;
}

void TransferSession::ResetMemory() noexcept
{
    memory_.active = false;
    memory_.imageIndex = 0;
    memory_.nextRow = 0;
    memory_.image = RasterImage{};
}

bool TransferSession::HasMemoryForPendingImage() const noexcept
{
    return memory_.active && memory_.imageIndex == pendingImageIndex_;
}

bool TransferSession::MemoryHasRowsRemaining() const noexcept
{
    return memory_.nextRow < memory_.image.height;
}

void TransferSession::BeginMemory(TW_UINT32 imageIndex, RasterImage&& image) noexcept
{
    memory_.active = true;
    memory_.imageIndex = imageIndex;
    memory_.nextRow = 0;
    memory_.image = std::move(image);
}

void TransferSession::AdvanceMemoryRows(TW_UINT32 rows) noexcept
{
    memory_.nextRow += rows;
}

bool TransferSession::MemoryComplete() const noexcept
{
    return memory_.nextRow >= memory_.image.height;
}

} // namespace mbf::twain
